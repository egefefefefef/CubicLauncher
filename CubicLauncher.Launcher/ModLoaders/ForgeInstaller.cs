using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CubicLauncher.Launcher.Downloads;
using CubicLauncher.Launcher.Installation;
using CubicLauncher.Launcher.Java;
using CubicLauncher.Launcher.VersionManager;

namespace CubicLauncher.Launcher.ModLoaders;

public sealed class ForgeInstaller(LoaderCatalog catalog, VerifiedDownloader downloader, JavaProvisioner java, ILogger<ForgeInstaller> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record ForgeProfile(string Id, string InheritsFrom, string MainClass, ModernArguments? Arguments, List<GameLibrary> Libraries);
    internal sealed record Overlay(string MainClass, ModernArguments? Arguments, List<GameLibrary> Libraries);

    internal async Task<Overlay> PrepareAsync(string root, LoaderRecipe recipe, VersionMetadata inherited, string? javaPath,
        Action<JavaProgress> onJava, CancellationToken token)
    {
        var endpoint = LoaderCatalog.InstallerUri(recipe);
        var sha = await catalog.ReadSha1Async(endpoint, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The official installer did not provide a SHA-1 checksum.");
        var installer = InstallPaths.Under(Path.Combine(root, "loader-installers"), recipe.Id + ".jar");
        await downloader.FetchAsync(new Artifact(endpoint.AbsoluteUri, sha, 0, null), installer, null, token).ConfigureAwait(false);
        var profile = await ReadProfileAsync(installer, recipe, token).ConfigureAwait(false);
        var runtime = await java.EnsureAsync(root, javaPath, inherited.JavaVersion ?? new JavaRequirement(8), new InlineProgress(onJava), token).ConfigureAwait(false);

        // The official installer gets a synthetic launcher profile and vanilla input in an isolated working directory.
        // This is not a process sandbox; only verified artifacts from the official repositories are executed.
        var stage = Path.Combine(root, $".forge-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            Directory.CreateDirectory(InstallPaths.VersionDirectory(stage, recipe.Base.Id));
            await CopyAsync(InstallPaths.ClientFile(root, recipe.Base.Id), InstallPaths.ClientFile(stage, recipe.Base.Id), token).ConfigureAwait(false);
            await CopyAsync(InstallPaths.MetadataFile(root, recipe.Base.Id), InstallPaths.MetadataFile(stage, recipe.Base.Id), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(stage, "launcher_profiles.json"), """{"profiles":{}}""", token).ConfigureAwait(false);
            var installerCopy = Path.Combine(stage, "installer.jar");
            await CopyAsync(installer, installerCopy, token).ConfigureAwait(false);
            var logDirectory = Path.Combine(root, "installer-logs");
            Directory.CreateDirectory(logDirectory);
            var log = Path.Combine(logDirectory, $"{recipe.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.log");
            await RunAsync(runtime, installerCopy, stage, log, token).ConfigureAwait(false);
            if (!File.Exists(InstallPaths.MetadataFile(stage, profile.Id)))
                throw new InvalidDataException($"{recipe.Kind} did not install the expected client profile. Log: {log}");

            var libraries = new List<GameLibrary>();
            foreach (var library in profile.Libraries.Where(lib => RuleEvaluator.Allows(lib.Rules, MinecraftPlatform.Current)))
            {
                var declared = library.Downloads?.Artifact;
                var relative = declared?.Path ?? LoaderCatalog.LibraryRelativePath(library.Name);
                var installed = VanillaInstaller.LibraryPath(stage, relative);
                if (!File.Exists(installed) || (declared is not null && !await VerifiedDownloader.FileMatchesAsync(declared, installed, token).ConfigureAwait(false)))
                    throw new InvalidDataException($"Missing or invalid {recipe.Kind} library: {library.Name}. Log: {log}");
                var artifact = declared is not null ? declared with { Path = relative } : new Artifact("", null, new FileInfo(installed).Length, relative);
                libraries.Add(library with { Downloads = new LibraryDownloads(artifact, library.Downloads?.Classifiers) });
            }
            await PromoteLibrariesAsync(stage, root, token).ConfigureAwait(false);
            logger.LogInformation("Processed {Loader} {Version}; installer log: {LogFile}", recipe.Kind, recipe.Loader.Version, log);
            return new Overlay(profile.MainClass, profile.Arguments, libraries);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    private static async Task<ForgeProfile> ReadProfileAsync(string installer, LoaderRecipe recipe, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(installer);
        if (zip.GetEntry("version.json") is not { Length: > 0 and < 4 * 1024 * 1024 } entry)
            throw new InvalidDataException("Unsupported Forge or NeoForge client installer format.");
        await using var stream = entry.Open();
        var profile = await JsonSerializer.DeserializeAsync<ForgeProfile>(stream, Json, token).ConfigureAwait(false);
        if (profile is null || profile.InheritsFrom != recipe.Base.Id || string.IsNullOrWhiteSpace(profile.MainClass) ||
            profile.Arguments is null || profile.Libraries is not { Count: > 0 })
            throw new InvalidDataException("The installer profile does not match the selected Minecraft version.");
        _ = InstallPaths.Segment(profile.Id);
        return profile;
    }

    private static async Task RunAsync(JavaRuntime runtime, string jar, string stage, string log, CancellationToken token)
    {
        var start = new ProcessStartInfo(runtime.Executable) { WorkingDirectory = stage, UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-Djava.awt.headless=true", $"-Duser.home={stage}", "-jar", jar, "--installClient", stage })
            start.ArgumentList.Add(arg);
        // Do not forward user JVM overrides or OAuth credentials to a third-party installer.
        start.Environment.Remove("JAVA_TOOL_OPTIONS");
        start.Environment.Remove("_JAVA_OPTIONS");
        start.Environment.Remove("JDK_JAVA_OPTIONS");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        await using var logWriter = new StreamWriter(log) { AutoFlush = true };
        var writer = TextWriter.Synchronized(logWriter);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the official loader installer.");
        async Task DrainAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
                await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        var output = DrainAsync(process.StandardOutput);
        var error = DrainAsync(process.StandardError);
        try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"The loader installer timed out. Log: {log}");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Loader installer exited with code {process.ExitCode}. Log: {log}");
    }

    private static async Task PromoteLibrariesAsync(string stage, string root, CancellationToken token)
    {
        var libraryRoot = Path.Combine(stage, "libraries");
        var pending = new Stack<string>();
        pending.Push(libraryRoot);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Unexpected symbolic link in the loader library directory.");
                if ((attrs & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                var relative = Path.GetRelativePath(libraryRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                var destination = VanillaInstaller.LibraryPath(root, relative);
                var temporary = destination + $".{Guid.NewGuid():N}.partial";
                try { await CopyAsync(path, temporary, token).ConfigureAwait(false); File.Move(temporary, destination, overwrite: true); }
                finally { File.Delete(temporary); }
            }
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }

    private sealed class InlineProgress(Action<JavaProgress> report) : IProgress<JavaProgress>
    {
        public void Report(JavaProgress value) => report(value);
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CubicLauncher.Launcher.Downloads;
using CubicLauncher.Launcher.Installation;
using CubicLauncher.Launcher.VersionManager;

namespace CubicLauncher.Launcher.Java;

public sealed record JavaProgress(int FilesReady, int FilesTotal);

public sealed class JavaProvisioner(HttpClient http, VerifiedDownloader downloader, JavaDetector detector, ILogger<JavaProvisioner> logger)
{
    public const string CatalogUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1);
    private sealed record RuntimeRelease(Artifact Manifest, RuntimeVersion Version);
    private sealed record RuntimeVersion(string Name, DateTimeOffset Released);
    private sealed record RuntimeManifest(Dictionary<string, RuntimeEntry> Files);
    private sealed record RuntimeEntry(string Type, bool Executable, string? Target, RuntimeDownloads? Downloads);
    private sealed record RuntimeDownloads(Artifact Raw);

    public async Task<JavaRuntime> EnsureAsync(string root, string? explicitPath, JavaRequirement requirement,
        IProgress<JavaProgress>? progress, CancellationToken token)
    {
        var platform = MinecraftPlatform.Current;
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return await detector.FindAsync(explicitPath, requirement.MajorVersion, token, platform.Architecture).ConfigureAwait(false);
        var component = Component(requirement);
        var directory = RuntimeDirectory(root, component, requirement.MajorVersion, platform);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (await ReadInstalledAsync(directory, requirement.MajorVersion, platform, token).ConfigureAwait(false) is { } installed)
                return installed;
            try { return await detector.FindAsync(null, requirement.MajorVersion, token, platform.Architecture).ConfigureAwait(false); }
            catch (JavaNotFoundException) { }
            return await InstallAsync(directory, component, requirement.MajorVersion, platform, progress, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<JavaRuntime> InstallManagedAsync(string root, JavaRequirement requirement, IProgress<JavaProgress>? progress, CancellationToken token)
    {
        var platform = MinecraftPlatform.Current;
        var component = Component(requirement);
        var directory = RuntimeDirectory(root, component, requirement.MajorVersion, platform);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await ReadInstalledAsync(directory, requirement.MajorVersion, platform, token).ConfigureAwait(false)
                ?? await InstallAsync(directory, component, requirement.MajorVersion, platform, progress, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<JavaRuntime?> FindInstalledAsync(string root, JavaRequirement requirement, CancellationToken token)
    {
        var platform = MinecraftPlatform.Current;
        return ReadInstalledAsync(RuntimeDirectory(root, Component(requirement), requirement.MajorVersion, platform),
            requirement.MajorVersion, platform, token);
    }

    private static string Component(JavaRequirement requirement) => requirement.Component ?? (requirement.MajorVersion switch
    {
        8 => "jre-legacy", 16 => "java-runtime-alpha", 17 => "java-runtime-gamma", 21 => "java-runtime-delta",
        _ => throw new InvalidOperationException($"Minecraft did not specify a runtime component for Java {requirement.MajorVersion}.")
    });

    public static string PlatformKey(MinecraftPlatform platform) => (platform.Name, platform.Architecture) switch
    {
        ("windows", "x86_64") => "windows-x64", ("linux", "x86_64") => "linux",
        ("osx", "x86_64") => "mac-os", ("osx", "aarch64") => "mac-os-arm64",
        _ => throw new NotSupportedException($"Automatic Java installation is unavailable on {platform.Name}/{platform.Architecture}.")
    };

    private static string RuntimeDirectory(string root, string component, int major, MinecraftPlatform platform) =>
        InstallPaths.Under(Path.Combine(root, "runtimes"), $"{PlatformKey(platform)}/{major}-{InstallPaths.Segment(component)}");

    private async Task<JavaRuntime?> ReadInstalledAsync(string directory, int major, MinecraftPlatform platform, CancellationToken token)
    {
        var receipt = Path.Combine(directory, ".cubiclauncher-java");
        if (!File.Exists(receipt)) return null;
        try
        {
            var relative = await File.ReadAllTextAsync(receipt, token).ConfigureAwait(false);
            var java = await JavaDetector.ProbeAsync(InstallPaths.Under(directory, relative), token).ConfigureAwait(false);
            return java.MajorVersion == major && JavaDetector.MatchesArchitecture(java, platform.Architecture) ? java : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { logger.LogWarning(ex, "Cached Java runtime could not be validated"); return null; }
    }

    private async Task<JavaRuntime> InstallAsync(string directory, string component, int major, MinecraftPlatform platform,
        IProgress<JavaProgress>? progress, CancellationToken token)
    {
        progress?.Report(new JavaProgress(0, 0));
        var catalog = await http.GetFromJsonAsync<Dictionary<string, Dictionary<string, RuntimeRelease[]>>>(CatalogUrl, token).ConfigureAwait(false);
        var release = catalog?.GetValueOrDefault(PlatformKey(platform))?.GetValueOrDefault(component)?.OrderByDescending(r => r.Version.Released).FirstOrDefault()
            ?? throw new InvalidOperationException($"Mojang does not provide {component} (Java {major}) for {PlatformKey(platform)}. Configure Java manually in Profiles.");
        var stage = directory + $".{Guid.NewGuid():N}.partial";
        Directory.CreateDirectory(stage);
        try
        {
            var manifestPath = Path.Combine(stage, ".runtime-manifest.json");
            await downloader.FetchAsync(Trusted(release.Manifest), manifestPath, null, token).ConfigureAwait(false);
            RuntimeManifest manifest;
            await using (var stream = File.OpenRead(manifestPath))
                manifest = await JsonSerializer.DeserializeAsync<RuntimeManifest>(stream, Json, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Empty Mojang Java manifest.");
            if (manifest.Files.Count is 0 or > 20000) throw new InvalidDataException("Unexpected Java manifest size.");
            var targets = manifest.Files.Select(pair => (Name: pair.Key, Path: InstallPaths.Under(stage, pair.Key), Entry: pair.Value)).ToArray();
            var seen = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
                { manifestPath, Path.Combine(stage, ".cubiclauncher-java") };
            if (targets.Any(target => !seen.Add(target.Path) || target.Entry.Type is not ("directory" or "file" or "link")))
                throw new InvalidDataException("The Java manifest contains an invalid or duplicate path.");
            var files = targets.Where(t => t.Entry.Type == "file").ToArray();
            long bytes = files.Sum(file => Trusted(file.Entry.Downloads?.Raw).Size);
            if (bytes > 512L * 1024 * 1024) throw new InvalidDataException("The Java runtime exceeds the supported size.");
            foreach (var target in targets.Where(t => t.Entry.Type == "directory")) Directory.CreateDirectory(target.Path);
            var done = 0;
            progress?.Report(new JavaProgress(0, files.Length));
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (file, ct) =>
            {
                await downloader.FetchAsync(Trusted(file.Entry.Downloads?.Raw), file.Path, null, ct).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows() && file.Entry.Executable)
                    File.SetUnixFileMode(file.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                var count = Interlocked.Increment(ref done);
                if (count % 10 == 0 || count == files.Length) progress?.Report(new JavaProgress(count, files.Length));
            }).ConfigureAwait(false);
            foreach (var link in targets.Where(t => t.Entry.Type == "link"))
            {
                token.ThrowIfCancellationRequested();
                var parent = Path.GetDirectoryName(link.Path)!;
                var target = RelativeLinkTarget(stage, parent, link.Entry.Target);
                Directory.CreateDirectory(parent);
                var relative = Path.GetRelativePath(parent, target);
                if (OperatingSystem.IsWindows() && Directory.Exists(target)) Directory.CreateSymbolicLink(link.Path, relative);
                else File.CreateSymbolicLink(link.Path, relative);
            }
            var javaName = platform.Name == "windows" ? "bin/java.exe" : "bin/java";
            var executable = targets.Where(t => t.Entry.Type is "file" or "link")
                .FirstOrDefault(t => t.Name == javaName || t.Name.EndsWith('/' + javaName, StringComparison.Ordinal));
            if (executable.Name is null) throw new InvalidDataException("The runtime did not include a Java executable.");
            var verified = await JavaDetector.ProbeAsync(executable.Path, token).ConfigureAwait(false);
            if (verified.MajorVersion != major || !JavaDetector.MatchesArchitecture(verified, platform.Architecture))
                throw new InvalidDataException("The installed Java runtime is the wrong version or architecture.");
            await File.WriteAllTextAsync(Path.Combine(stage, ".cubiclauncher-java"), executable.Name, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.Move(stage, directory);
            logger.LogInformation("Installed Mojang runtime Java {Version} ({Component})", verified.Version, component);
            return verified with { Executable = InstallPaths.Under(directory, executable.Name) };
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    public static string RelativeLinkTarget(string root, string parent, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':'))
            throw new InvalidDataException("Invalid Java runtime link target.");
        var target = Path.GetFullPath(Path.Combine(parent, relative));
        var local = Path.GetRelativePath(root, target).Replace(Path.DirectorySeparatorChar, '/');
        return InstallPaths.Under(root, local);
    }

    private static Artifact Trusted(Artifact? artifact)
    {
        if (artifact is not { Sha1.Length: 40, Size: >= 0 } || !artifact.Sha1.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Host is not ("launchermeta.mojang.com" or "launcher.mojang.com" or "piston-meta.mojang.com" or "piston-data.mojang.com"))
            throw new InvalidDataException("Java files must have an official Mojang HTTPS URL and SHA-1.");
        return artifact;
    }
}

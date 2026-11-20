using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoxelNest.Core.Models;
using VoxelNest.Launcher.Authentication;
using VoxelNest.Launcher.Downloads;
using VoxelNest.Launcher.Installation;
using VoxelNest.Launcher.Java;
using VoxelNest.Launcher.VersionManager;

namespace VoxelNest.Launcher.GameRunner;

public sealed record GameResult(int ExitCode, string LogFile);

public sealed class MinecraftRunner(JavaDetector java, VanillaInstaller installer, ILogger<MinecraftRunner> logger)
{
    public async Task<GameResult> RunAsync(string root, string version, PlayerProfile profile, MinecraftSession session,
        IProgress<int> starting, CancellationToken token)
    {
        if (!installer.IsInstalled(root, version)) throw new InvalidOperationException("Install this Minecraft version first.");
        if (session.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Microsoft sign-in expired. Sign in again.");
        if (profile.MinimumRamMb < 256 || profile.MaximumRamMb < profile.MinimumRamMb)
            throw new InvalidOperationException("The profile has invalid memory settings.");
        var metadata = await VanillaInstaller.ReadAsync<VersionMetadata>(InstallPaths.MetadataFile(root, version), token).ConfigureAwait(false);
        var clientJar = InstallPaths.ClientFile(root, version);
        if (!metadata.Downloads.TryGetValue("client", out var client) ||
            !await VerifiedDownloader.FileMatchesAsync(client, clientJar, token).ConfigureAwait(false))
            throw new InvalidDataException("Minecraft's client failed verification. Select Verify / repair in Versions.");
        var platform = MinecraftPlatform.Current;
        var javaRuntime = await java.FindAsync(profile.JavaPath, metadata.JavaVersion?.MajorVersion ?? 8, token, platform.Architecture).ConfigureAwait(false);
        var instance = profile.GameDirectory(root);
        Directory.CreateDirectory(instance);
        var assetsRoot = Path.Combine(root, "assets");
        var gameAssets = await PrepareLegacyAssetsAsync(metadata, assetsRoot, instance, token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(metadata.MinecraftArguments) && platform.Architecture == "aarch64")
            throw new InvalidOperationException("This legacy version has no supported ARM64 native libraries. Use an x64 environment.");
        var paths = metadata.Libraries.Where(l => RuleEvaluator.Allows(l.Rules, platform))
            .Select(l => l.Downloads?.Artifact?.Path).OfType<string>().Select(p => VanillaInstaller.LibraryPath(root, p))
            .Append(clientJar).Distinct().ToArray();
        foreach (var path in paths)
            if (!File.Exists(path)) throw new FileNotFoundException("An installed file is missing. Reinstall this version.", path);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = session.Name, ["auth_uuid"] = session.Id,
            ["auth_access_token"] = session.AccessToken, ["auth_session"] = $"token:{session.AccessToken}:{session.Id}",
            ["auth_xuid"] = "", ["clientid"] = "", ["user_type"] = "msa", ["user_properties"] = "{}", ["profile_properties"] = "{}",
            ["version_name"] = version, ["version_type"] = metadata.Type, ["game_directory"] = instance,
            ["assets_root"] = assetsRoot, ["game_assets"] = gameAssets, ["assets_index_name"] = metadata.AssetIndex?.Id ?? metadata.Assets ?? "legacy",
            ["natives_directory"] = InstallPaths.NativeDirectory(root, version), ["launcher_name"] = "VoxelNest",
            ["launcher_version"] = "0.1.0", ["classpath"] = string.Join(Path.PathSeparator, paths),
            ["library_directory"] = Path.Combine(root, "libraries"), ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["resolution_width"] = profile.Width.ToString(), ["resolution_height"] = profile.Height.ToString()
        };
        var features = new Dictionary<string, bool> { ["has_custom_resolution"] = !profile.Fullscreen };
        var start = new ProcessStartInfo(javaRuntime.Executable)
        {
            WorkingDirectory = instance, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add($"-Xms{profile.MinimumRamMb}m");
        start.ArgumentList.Add($"-Xmx{profile.MaximumRamMb}m");
        start.ArgumentList.Add("-Dlog4j2.formatMsgNoLookups=true");
        foreach (var arg in ArgumentBuilder.Tokenize(profile.JvmArguments)) start.ArgumentList.Add(arg);
        var jvm = metadata.Arguments?.Jvm is { } jvmArguments
            ? ArgumentBuilder.Read(jvmArguments, platform, features)
            : new[] { "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" };
        if (metadata.Arguments?.Jvm is null && OperatingSystem.IsMacOS()) start.ArgumentList.Add("-XstartOnFirstThread");
        foreach (var arg in jvm) start.ArgumentList.Add(ArgumentBuilder.Expand(arg, variables));
        if (metadata.Logging?.GetValueOrDefault("client") is { } logging)
        {
            variables["path"] = VanillaInstaller.LoggingPath(root, logging.File.Id);
            if (!File.Exists(variables["path"])) throw new FileNotFoundException("Logging configuration is missing. Reinstall this version.");
            start.ArgumentList.Add(ArgumentBuilder.Expand(logging.Argument, variables));
        }
        start.ArgumentList.Add(metadata.MainClass);
        var game = (metadata.Arguments?.Game is { } gameArguments
            ? ArgumentBuilder.Read(gameArguments, platform, features)
            : ArgumentBuilder.Tokenize(metadata.MinecraftArguments)).ToList();
        if (!profile.Fullscreen && metadata.Type is "release" or "snapshot" && !game.Contains("--width"))
            game.AddRange(["--width", "${resolution_width}", "--height", "${resolution_height}"]);
        if (profile.Fullscreen && metadata.Type is "release" or "snapshot" && !game.Contains("--fullscreen")) game.AddRange(["--fullscreen", "true"]);
        foreach (var arg in game) start.ArgumentList.Add(ArgumentBuilder.Expand(arg, variables));

        var logDirectory = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logDirectory);
        var logFile = Path.Combine(logDirectory, $"voxelnest-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        await using var writer = new StreamWriter(logFile) { AutoFlush = true };
        using var synchedWriter = TextWriter.Synchronized(writer);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Minecraft did not start.");
        starting.Report(process.Id);
        logger.LogInformation("Started Minecraft {Version} (PID {ProcessId}, Java {Java})", version, process.Id, javaRuntime.Version);
        var output = PipeAsync(process.StandardOutput, synchedWriter, session.AccessToken, "OUT");
        var errors = PipeAsync(process.StandardError, synchedWriter, session.AccessToken, "ERR");
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(output, errors).ConfigureAwait(false);
        logger.LogInformation("Minecraft exited with code {ExitCode}", process.ExitCode);
        return new GameResult(process.ExitCode, logFile);
    }

    private static async Task PipeAsync(StreamReader reader, TextWriter log, string secret, string channel)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            await log.WriteLineAsync($"[{channel}] {line.Replace(secret, "[redacted]", StringComparison.Ordinal)}").ConfigureAwait(false);
    }

    private static async Task<string> PrepareLegacyAssetsAsync(VersionMetadata metadata, string assets, string instance, CancellationToken token)
    {
        if (metadata.AssetIndex is not { } index) return assets;
        var indexFile = InstallPaths.Under(Path.Combine(assets, "indexes"), InstallPaths.Segment(index.Id) + ".json");
        var manifest = await VanillaInstaller.ReadAsync<AssetIndex>(indexFile, token).ConfigureAwait(false);
        if (!manifest.Virtual && !manifest.MapToResources) return assets;
        var target = manifest.MapToResources ? Path.Combine(instance, "resources") : Path.Combine(assets, "virtual", index.Id);
        foreach (var (logical, asset) in manifest.Objects)
        {
            token.ThrowIfCancellationRequested();
            if (asset.Hash.Length != 40 || !asset.Hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid asset hash.");
            var source = InstallPaths.Under(Path.Combine(assets, "objects"), $"{asset.Hash[..2]}/{asset.Hash}");
            var destination = InstallPaths.Under(target, logical);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(source, destination);
        }
        return target;
    }
}

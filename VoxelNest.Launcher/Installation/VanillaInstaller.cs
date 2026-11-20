using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VoxelNest.Launcher.Downloads;
using VoxelNest.Launcher.VersionManager;

namespace VoxelNest.Launcher.Installation;

public sealed record InstallProgress(int FilesDone, int FilesTotal, long BytesRemaining, double BytesPerSecond)
{
    public double Percent => FilesTotal > 0 ? 100.0 * FilesDone / FilesTotal : 0;
}

public sealed class VanillaInstaller(VerifiedDownloader downloader, ILogger<VanillaInstaller> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record InstallFile(Artifact Artifact, string Target);
    private sealed record NativeFile(string Target, IReadOnlyList<string> Excludes);

    public bool IsInstalled(string root, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            return File.Exists(InstallPaths.Receipt(root, id)) && File.Exists(InstallPaths.ClientFile(root, id)) &&
                File.Exists(InstallPaths.MetadataFile(root, id)) && Directory.Exists(InstallPaths.NativeDirectory(root, id));
        }
        catch (InvalidDataException) { return false; }
    }

    public async Task InstallAsync(string root, VersionEntry version, IProgress<InstallProgress> progress, CancellationToken token)
    {
        var metadataPath = InstallPaths.MetadataFile(root, version.Id);
        await downloader.FetchAsync(new Artifact(version.Url, version.Sha1, 0, null), metadataPath, null, token).ConfigureAwait(false);
        var metadata = await ReadAsync<VersionMetadata>(metadataPath, token).ConfigureAwait(false);
        if (metadata.Id != version.Id || !metadata.Downloads.TryGetValue("client", out var client))
            throw new InvalidDataException("The version metadata does not contain the expected client.");

        var files = new List<InstallFile> { new(client, InstallPaths.ClientFile(root, version.Id)) };
        if (metadata.Logging?.GetValueOrDefault("client")?.File is { } log)
            files.Add(new InstallFile(new Artifact(log.Url, log.Sha1, log.Size, null), LoggingPath(root, log.Id)));
        var natives = new List<NativeFile>();
        var platform = MinecraftPlatform.Current;
        foreach (var library in metadata.Libraries.Where(l => RuleEvaluator.Allows(l.Rules, platform)))
        {
            if (library.Downloads?.Artifact is { Path: not null } artifact)
                files.Add(new InstallFile(artifact, LibraryPath(root, artifact.Path)));
            if (library.Natives?.TryGetValue(platform.Name, out var key) != true || key is null) continue;
            key = key.Replace("${arch}", platform.Architecture == "x86" ? "32" : "64", StringComparison.Ordinal);
            if (library.Downloads?.Classifiers?.GetValueOrDefault(key) is not { Path: not null } native)
                throw new InvalidDataException($"No native library for {platform.Name}: {library.Name}");
            var path = LibraryPath(root, native.Path);
            files.Add(new InstallFile(native, path));
            natives.Add(new NativeFile(path, library.Extract?.Exclude ?? ["META-INF/"]));
        }

        if (metadata.AssetIndex is { } index)
        {
            var indexPath = InstallPaths.Under(Path.Combine(root, "assets", "indexes"), InstallPaths.Segment(index.Id) + ".json");
            await downloader.FetchAsync(new Artifact(index.Url, index.Sha1, index.Size, null), indexPath, null, token).ConfigureAwait(false);
            var assets = await ReadAsync<AssetIndex>(indexPath, token).ConfigureAwait(false);
            foreach (var asset in assets.Objects.Values.DistinctBy(a => a.Hash, StringComparer.OrdinalIgnoreCase))
            {
                if (asset.Hash.Length != 40 || !asset.Hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid asset hash.");
                var objectPath = $"{asset.Hash[..2]}/{asset.Hash}";
                files.Add(new InstallFile(new Artifact($"https://resources.download.minecraft.net/{objectPath}", asset.Hash, asset.Size, null),
                    InstallPaths.Under(Path.Combine(root, "assets", "objects"), objectPath)));
            }
        }

        // Build the actual network plan so files already present don't inflate bytes remaining.
        files = files.DistinctBy(f => f.Target, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
        var missing = new System.Collections.Concurrent.ConcurrentBag<InstallFile>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = token },
            async (file, ct) => { if (!await VerifiedDownloader.FileMatchesAsync(file.Artifact, file.Target, ct).ConfigureAwait(false)) missing.Add(file); });
        long remaining = missing.Sum(file => file.Artifact.Size);
        long received = 0;
        var done = files.Count - missing.Count;
        var total = files.Count + 1; // Native preparation is the last step.
        var lastReport = 0L;
        var stopwatch = Stopwatch.StartNew();
        void Report(bool force = false)
        {
            var now = stopwatch.ElapsedMilliseconds;
            var previous = Interlocked.Read(ref lastReport);
            if (!force && (now - previous < 150 || Interlocked.CompareExchange(ref lastReport, now, previous) != previous)) return;
            progress.Report(new InstallProgress(Volatile.Read(ref done), total, Math.Max(0, Interlocked.Read(ref remaining)),
                Interlocked.Read(ref received) / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds)));
        }
        Report(true);
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            async (file, ct) =>
            {
                await downloader.FetchAsync(file.Artifact, file.Target, bytes =>
                {
                    if (file.Artifact.Size > 0) Interlocked.Add(ref remaining, -bytes);
                    Interlocked.Add(ref received, bytes);
                    Report();
                }, ct).ConfigureAwait(false);
                Interlocked.Increment(ref done);
                Report();
            });

        var nativeDirectory = InstallPaths.NativeDirectory(root, version.Id);
        var temp = nativeDirectory + $".{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var native in natives)
                await NativeExtractor.ExtractAsync(native.Target, temp, native.Excludes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(nativeDirectory)) Directory.Delete(nativeDirectory, recursive: true);
            Directory.Move(temp, nativeDirectory);
            await File.WriteAllTextAsync(InstallPaths.Receipt(root, version.Id), version.Id, token).ConfigureAwait(false);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
        Interlocked.Increment(ref done);
        Report(true);
        logger.LogInformation("Installed official Minecraft version {Version} ({Count} files)", version.Id, files.Count);
    }

    public static string LoggingPath(string root, string id) => InstallPaths.Under(Path.Combine(root, "assets", "log_configs"), InstallPaths.Segment(id));

    public static string LibraryPath(string root, string relative) => InstallPaths.Under(Path.Combine(root, "libraries"), relative);

    public Task DeleteAsync(string root, string id, CancellationToken token) => Task.Run(() =>
    {
        var directory = InstallPaths.VersionDirectory(root, id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        logger.LogInformation("Removed Minecraft version {Version}; shared assets and game worlds were retained", id);
    }, token);

    public static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, token).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Empty metadata: {Path.GetFileName(path)}");
    }
}

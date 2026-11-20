using System.IO.Compression;

namespace VoxelNest.Launcher.Installation;

public static class NativeExtractor
{
    public static async Task ExtractAsync(string archive, string destination, IReadOnlyList<string> excludes, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0 || excludes.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.Ordinal))) continue;
            var path = InstallPaths.Under(destination, entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000)
                throw new InvalidDataException("A native archive contains a symbolic link.");
            if (entry.Length > 256 * 1024 * 1024 - total)
                throw new InvalidDataException("Native archive expands beyond the allowed size.");
            total += entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
    }
}

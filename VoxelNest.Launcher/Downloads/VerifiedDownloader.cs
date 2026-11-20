using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using VoxelNest.Launcher.VersionManager;

namespace VoxelNest.Launcher.Downloads;

public sealed class VerifiedDownloader(HttpClient client, ILogger<VerifiedDownloader> logger)
{
    public async Task FetchAsync(Artifact artifact, string target, Action<int>? bytesReceived, CancellationToken token)
    {
        if (await FileMatchesAsync(artifact, target, token).ConfigureAwait(false)) return;
        if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Downloads must use HTTPS.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + $".{Guid.NewGuid():N}.partial";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("An HTTPS download redirected to an insecure URL.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (artifact.Size > 0 && output.Length + count > artifact.Size)
                        throw new InvalidDataException($"Download exceeds its declared size: {Path.GetFileName(target)}");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    bytesReceived?.Invoke(count);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (!await FileMatchesAsync(artifact, temp, token).ConfigureAwait(false))
                throw new InvalidDataException($"Checksum or size mismatch: {Path.GetFileName(target)}");
            token.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
            logger.LogDebug("Verified {Target}", target);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Download timed out: {Path.GetFileName(target)}", ex);
        }
        finally { File.Delete(temp); }
    }

    public static async Task<bool> FileMatchesAsync(Artifact artifact, string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (artifact.Size > 0 && info.Length != artifact.Size) return false;
        if (string.IsNullOrWhiteSpace(artifact.Sha1)) return info.Length > 0;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA1.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(digest).Equals(artifact.Sha1, StringComparison.OrdinalIgnoreCase);
    }
}

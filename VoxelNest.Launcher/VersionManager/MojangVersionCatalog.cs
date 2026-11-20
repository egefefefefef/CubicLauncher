using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace VoxelNest.Launcher.VersionManager;

public sealed class MojangVersionCatalog(HttpClient client, ILogger<MojangVersionCatalog> logger)
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public async Task<IReadOnlyList<VersionEntry>> FetchAsync(CancellationToken cancellationToken)
    {
        var manifest = await client.GetFromJsonAsync<VersionManifest>(ManifestUrl, cancellationToken).ConfigureAwait(false);
        if (manifest is null || manifest.Versions.Count == 0)
            throw new InvalidDataException("Mojang returned an empty version catalog.");
        logger.LogInformation("Fetched {Count} official Minecraft versions", manifest.Versions.Count);
        return manifest.Versions;
    }
}

using System.Net;
using System.Net.Http.Json;
using VoxelNest.Launcher.Installation;
using VoxelNest.Launcher.VersionManager;

namespace VoxelNest.Launcher.ModLoaders;

public enum LoaderKind { Fabric, Quilt }
public sealed record LoaderRelease(string Version, bool Stable);
internal sealed record LoaderIndexEntry(LoaderRelease Loader);
internal sealed record LoaderLibrary(string Name, string? Url, string? Sha1, long Size, List<PlatformRule>? Rules);
internal sealed record LoaderProfile(string Id, string InheritsFrom, string MainClass, List<LoaderLibrary> Libraries, ModernArguments? Arguments);
public sealed record LoaderRecipe(VersionEntry Base, LoaderKind Kind, LoaderRelease Loader)
{
    public string Id => InstallPaths.Segment($"{Kind.ToString().ToLowerInvariant()}-loader-{Loader.Version}-{Base.Id}");
}

public sealed class LoaderCatalog(HttpClient client)
{
    private static string Endpoint(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "https://meta.fabricmc.net/v2/versions/loader/",
        LoaderKind.Quilt => "https://meta.quiltmc.org/v3/versions/loader/",
        _ => throw new ArgumentOutOfRangeException(nameof(loader))
    };

    public async Task<IReadOnlyList<LoaderRelease>> FindAsync(LoaderKind kind, string version, CancellationToken token)
    {
        using var response = await client.GetAsync(Endpoint(kind) + Uri.EscapeDataString(version), token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return [];
        response.EnsureSuccessStatusCode();
        var records = await response.Content.ReadFromJsonAsync<LoaderIndexEntry[]>(token).ConfigureAwait(false);
        return records?.Select(record => record.Loader).Where(loader => loader is not null && !string.IsNullOrWhiteSpace(loader.Version))
            .DistinctBy(loader => loader.Version).ToArray() ?? [];
    }

    internal async Task<LoaderProfile> GetProfileAsync(LoaderRecipe recipe, CancellationToken token)
    {
        var url = $"{Endpoint(recipe.Kind)}{Uri.EscapeDataString(recipe.Base.Id)}/{Uri.EscapeDataString(recipe.Loader.Version)}/profile/json";
        var profile = await client.GetFromJsonAsync<LoaderProfile>(url, token).ConfigureAwait(false);
        if (profile is null || profile.InheritsFrom != recipe.Base.Id || string.IsNullOrWhiteSpace(profile.MainClass) || profile.Libraries is not { Count: > 0 })
            throw new InvalidDataException("The loader profile does not match the requested Minecraft version.");
        return profile;
    }

    internal async Task<string?> ReadSha1Async(Uri artifact, CancellationToken token)
    {
        using var response = await client.GetAsync(artifact.AbsoluteUri + ".sha1", token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var sha = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException("The Maven repository returned an invalid SHA-1 file.");
        return sha;
    }

    public static string LibraryRelativePath(string coordinate)
    {
        var fields = coordinate.Split(':');
        if (fields.Length is not (3 or 4)) throw new InvalidDataException($"Invalid Maven coordinate: {coordinate}");
        var group = string.Join("/", fields[0].Split('.').Select(InstallPaths.Segment));
        var artifact = InstallPaths.Segment(fields[1]);
        var version = InstallPaths.Segment(fields[2]);
        var classifier = fields.Length == 4 ? "-" + InstallPaths.Segment(fields[3]) : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }
}

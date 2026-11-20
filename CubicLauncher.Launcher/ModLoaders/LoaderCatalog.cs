using System.Net;
using System.Net.Http.Json;
using System.Xml;
using System.Xml.Linq;
using CubicLauncher.Launcher.Installation;
using CubicLauncher.Launcher.VersionManager;

namespace CubicLauncher.Launcher.ModLoaders;

public enum LoaderKind { Fabric, Quilt, Forge, NeoForge }
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
        if (IsForgeFamily(kind)) return await FindForgeFamilyAsync(kind, version, token).ConfigureAwait(false);
        using var response = await client.GetAsync(Endpoint(kind) + Uri.EscapeDataString(version), token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return [];
        response.EnsureSuccessStatusCode();
        var records = await response.Content.ReadFromJsonAsync<LoaderIndexEntry[]>(token).ConfigureAwait(false);
        return records?.Select(record => record.Loader).Where(loader => loader is not null && !string.IsNullOrWhiteSpace(loader.Version))
            .DistinctBy(loader => loader.Version).ToArray() ?? [];
    }

    internal static bool IsForgeFamily(LoaderKind kind) => kind is LoaderKind.Forge or LoaderKind.NeoForge;

    private static string MavenRoot(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => "https://maven.minecraftforge.net/net/minecraftforge/forge/",
        LoaderKind.NeoForge => "https://maven.neoforged.net/releases/net/neoforged/neoforge/",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string? ForgePrefix(LoaderKind kind, string minecraft)
    {
        if (!Version.TryParse(minecraft, out var parsed) || parsed.Major != 1 || parsed.Revision >= 0 || parsed.Minor is < 20 or > 21)
            return null;
        var patch = Math.Max(0, parsed.Build);
        if (parsed.Minor == 20 && patch < (kind == LoaderKind.NeoForge ? 2 : 1)) return null;
        return kind == LoaderKind.Forge ? minecraft + "-" : $"{parsed.Minor}.{patch}.";
    }

    private async Task<IReadOnlyList<LoaderRelease>> FindForgeFamilyAsync(LoaderKind kind, string minecraft, CancellationToken token)
    {
        var prefix = ForgePrefix(kind, minecraft);
        if (prefix is null) return [];
        await using var stream = await client.GetStreamAsync(MavenRoot(kind) + "maven-metadata.xml", token).ConfigureAwait(false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        var xml = await XDocument.LoadAsync(reader, LoadOptions.None, token).ConfigureAwait(false);
        return xml.Root?.Element("versioning")?.Element("versions")?.Elements("version")
            .Select(entry => entry.Value.Trim()).Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
            .Select(value => kind == LoaderKind.Forge ? value[prefix.Length..] : value)
            .Where(value => value.Length is > 0 and < 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => Version.TryParse(value.Split('-', 2)[0], out var number) ? number : new Version())
            .ThenBy(value => value.Contains('-'))
            .Select(value => new LoaderRelease(value, !value.Contains('-'))).Take(100).ToArray() ?? [];
    }

    public static Uri InstallerUri(LoaderRecipe recipe)
    {
        var prefix = ForgePrefix(recipe.Kind, recipe.Base.Id)
            ?? throw new NotSupportedException($"{recipe.Kind} client installation is not supported for Minecraft {recipe.Base.Id}.");
        var loader = InstallPaths.Segment(recipe.Loader.Version);
        if (recipe.Kind == LoaderKind.NeoForge && !loader.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("NeoForge does not match the selected Minecraft version.");
        var version = recipe.Kind == LoaderKind.Forge ? prefix + loader : loader;
        var artifact = recipe.Kind == LoaderKind.Forge ? "forge" : "neoforge";
        return new Uri($"{MavenRoot(recipe.Kind)}{version}/{artifact}-{version}-installer.jar");
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
        var pair = coordinate.Split('@');
        if (pair.Length > 2) throw new InvalidDataException($"Invalid Maven coordinate: {coordinate}");
        var extension = pair.Length == 2 ? InstallPaths.Segment(pair[1]) : "jar";
        var fields = pair[0].Split(':');
        if (fields.Length is not (3 or 4)) throw new InvalidDataException($"Invalid Maven coordinate: {coordinate}");
        var group = string.Join("/", fields[0].Split('.').Select(InstallPaths.Segment));
        var artifact = InstallPaths.Segment(fields[1]);
        var version = InstallPaths.Segment(fields[2]);
        var classifier = fields.Length == 4 ? "-" + InstallPaths.Segment(fields[3]) : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }
}

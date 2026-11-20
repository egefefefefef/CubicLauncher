using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxelNest.Launcher.VersionManager;

public sealed record VersionEntry(string Id, string Type, string Url, string? Sha1, DateTimeOffset ReleaseTime)
{
    [JsonIgnore] public string ReleaseDate => ReleaseTime.ToString("yyyy-MM-dd");
}

internal sealed record VersionManifest(List<VersionEntry> Versions);
public sealed record Artifact(string Url, string? Sha1, long Size, string? Path);
public sealed record JavaRequirement(int MajorVersion);
public sealed record PlatformRule(string Action, RuleOs? Os, Dictionary<string, bool>? Features);
public sealed record RuleOs(string? Name, string? Arch, string? Version);
public sealed record ExtractionRules(List<string>? Exclude);
public sealed record LibraryDownloads(Artifact? Artifact, Dictionary<string, Artifact>? Classifiers);
public sealed record GameLibrary(string Name, LibraryDownloads? Downloads, List<PlatformRule>? Rules,
    Dictionary<string, string>? Natives, ExtractionRules? Extract);
public sealed record AssetObject(string Hash, long Size);
public sealed record AssetIndex(Dictionary<string, AssetObject> Objects, bool Virtual, [property: JsonPropertyName("map_to_resources")] bool MapToResources);
public sealed record AssetIndexReference(string Id, string Url, string? Sha1, long Size);
public sealed record ModernArguments(List<JsonElement>? Game, List<JsonElement>? Jvm);
public sealed record VersionMetadata(string Id, string Type, string MainClass, string? Assets,
    AssetIndexReference? AssetIndex, JavaRequirement? JavaVersion, Dictionary<string, Artifact> Downloads,
    List<GameLibrary> Libraries, ModernArguments? Arguments, string? MinecraftArguments, Dictionary<string, ClientLogging>? Logging);
public sealed record LoggingFile(string Id, string Url, string? Sha1, long Size);
public sealed record ClientLogging(string Argument, LoggingFile File);

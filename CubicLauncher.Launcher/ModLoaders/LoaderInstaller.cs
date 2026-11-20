using System.Text.Json;
using Microsoft.Extensions.Logging;
using CubicLauncher.Launcher.Downloads;
using CubicLauncher.Launcher.Installation;
using CubicLauncher.Launcher.VersionManager;

namespace CubicLauncher.Launcher.ModLoaders;

public sealed class LoaderInstaller(LoaderCatalog catalog, VanillaInstaller vanilla, VerifiedDownloader downloader, ILogger<LoaderInstaller> logger, ForgeInstaller? forge = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class ForwardProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }

    public static string RecipeFile(string root, string id) => Path.Combine(InstallPaths.VersionDirectory(root, id), "loader-recipe.json");

    public async Task InstallAsync(string root, LoaderRecipe recipe, IProgress<InstallProgress> progress, CancellationToken token,
        string? javaPath = null)
    {
        var id = recipe.Id;
        var forgeFamily = LoaderCatalog.IsForgeFamily(recipe.Kind);
        var profile = forgeFamily ? null : await catalog.GetProfileAsync(recipe, token).ConfigureAwait(false);
        var applicable = profile?.Libraries.Where(l => RuleEvaluator.Allows(l.Rules, MinecraftPlatform.Current)).ToArray() ?? [];
        var baseFiles = 0;
        var steps = forgeFamily ? 2 : applicable.Length + 1;
        await vanilla.InstallAsync(root, recipe.Base, new ForwardProgress(p =>
        {
            baseFiles = p.FilesTotal;
            progress.Report(p with { FilesTotal = p.FilesTotal + steps });
        }), token).ConfigureAwait(false);
        var inherited = await VanillaInstaller.ReadAsync<VersionMetadata>(InstallPaths.MetadataFile(root, recipe.Base.Id), token).ConfigureAwait(false);
        List<GameLibrary> libraries;
        string mainClass;
        ModernArguments? args;
        if (forgeFamily)
        {
            if (forge is null) throw new InvalidOperationException("Forge installer service is not available.");
            progress.Report(new InstallProgress(baseFiles, baseFiles + steps, 0, 0, "ForgeInstallProgress"));
            var overlay = await forge.PrepareAsync(root, recipe, inherited, javaPath, p =>
                progress.Report(new InstallProgress(baseFiles, baseFiles + steps, 0, 0,
                    p.FilesTotal == p.FilesReady && p.FilesTotal > 0 ? "ForgeInstallProgress" : "JavaInstallProgress")), token).ConfigureAwait(false);
            (mainClass, args, libraries) = (overlay.MainClass, overlay.Arguments, overlay.Libraries);
            progress.Report(new InstallProgress(baseFiles + 1, baseFiles + steps, 0, 0));
        }
        else
        {
            mainClass = profile!.MainClass;
            args = profile.Arguments;
            libraries = [];
            foreach (var library in applicable)
            {
                var relative = LoaderCatalog.LibraryRelativePath(library.Name);
                var repository = new Uri((library.Url ?? "https://libraries.minecraft.net").TrimEnd('/') + "/");
                if (repository.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(repository.UserInfo) || repository.Host is not (
                    "maven.fabricmc.net" or "maven.quiltmc.org" or "libraries.minecraft.net" or "repo.maven.apache.org" or "repo1.maven.org"))
                    throw new InvalidDataException($"Unsupported Maven repository for {library.Name}.");
                var url = new Uri(repository, relative);
                var sha1 = library.Sha1 ?? await catalog.ReadSha1Async(url, token).ConfigureAwait(false);
                var artifact = new Artifact(url.AbsoluteUri, sha1, library.Size, relative);
                await downloader.FetchAsync(artifact, VanillaInstaller.LibraryPath(root, relative), null, token).ConfigureAwait(false);
                libraries.Add(new GameLibrary(library.Name, new LibraryDownloads(artifact, null), library.Rules, null, null));
                progress.Report(new InstallProgress(baseFiles + libraries.Count, baseFiles + steps, 0, 0));
            }
        }

        var supplied = libraries.Select(lib => LibraryIdentity(lib.Name, forgeFamily)).ToHashSet(StringComparer.Ordinal);
        var merged = inherited with
        {
            Id = id, MainClass = mainClass,
            Libraries = [.. libraries, .. inherited.Libraries.Where(lib => !supplied.Contains(LibraryIdentity(lib.Name, forgeFamily)))],
            Arguments = inherited.Arguments is null ? null : new ModernArguments(
                [.. inherited.Arguments.Game ?? [], .. args?.Game ?? []],
                [.. inherited.Arguments.Jvm ?? [], .. args?.Jvm ?? []])
        };
        var versionDirectory = InstallPaths.VersionDirectory(root, id);
        var temp = versionDirectory + $".{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(temp);
        try
        {
            await WriteAsync(Path.Combine(temp, id + ".json"), merged, token).ConfigureAwait(false);
            await WriteAsync(Path.Combine(temp, "loader-recipe.json"), recipe, token).ConfigureAwait(false);
            var baseNatives = InstallPaths.NativeDirectory(root, recipe.Base.Id);
            var modNatives = Path.Combine(temp, Path.GetFileName(baseNatives));
            Directory.CreateDirectory(modNatives);
            foreach (var file in Directory.EnumerateFiles(baseNatives, "*", SearchOption.AllDirectories))
                await CopyAsync(file, InstallPaths.Under(modNatives, Path.GetRelativePath(baseNatives, file).Replace(Path.DirectorySeparatorChar, '/')), token).ConfigureAwait(false);
            await CopyAsync(InstallPaths.ClientFile(root, recipe.Base.Id), Path.Combine(temp, id + ".jar"), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(temp, Path.GetFileName(InstallPaths.Receipt(root, id))), id, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(versionDirectory)) Directory.Delete(versionDirectory, recursive: true);
            Directory.Move(temp, versionDirectory);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
        progress.Report(new InstallProgress(baseFiles + steps, baseFiles + steps, 0, 0));
        logger.LogInformation("Installed {Loader} {Version} for Minecraft {Minecraft}", recipe.Kind, recipe.Loader.Version, recipe.Base.Id);
    }

    private static string LibraryIdentity(string name, bool replaceVersion)
    {
        if (!replaceVersion) return name;
        var pair = name.Split('@', 2);
        var fields = pair[0].Split(':');
        if (fields.Length is not (3 or 4)) return name;
        return $"{fields[0]}:{fields[1]}:{(fields.Length == 4 ? fields[3] : "")}@{(pair.Length == 2 ? pair[1] : "jar")}";
    }

    private static async Task WriteAsync<T>(string path, T data, CancellationToken token)
    {
        await using var file = File.Create(path);
        await JsonSerializer.SerializeAsync(file, data, Json, token).ConfigureAwait(false);
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoxelNest.Launcher.Downloads;
using VoxelNest.Launcher.Installation;
using VoxelNest.Launcher.ModLoaders;
using VoxelNest.Launcher.VersionManager;
using Xunit;

namespace VoxelNest.Tests;

public sealed class InstallerTests
{
    [Fact]
    public async Task InstallsVerifiedGameAndSharedAssetsAndRemovesOnlyVersion()
    {
        using var directory = new TemporaryDirectory();
        var token = TestContext.Current.CancellationToken;
        var client = Encoding.UTF8.GetBytes("mc-client-fixture");
        var asset = Encoding.UTF8.GetBytes("mc-asset-fixture");
        var assetHash = Sha(asset);
        var index = JsonSerializer.SerializeToUtf8Bytes(new { objects = new Dictionary<string, object>
        { ["example/sound.ogg"] = new { hash = assetHash, size = asset.Length } } });
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "1.100", type = "release", mainClass = "net.minecraft.client.main.Main", libraries = Array.Empty<object>(),
            downloads = new { client = new { url = "https://files.example/client", sha1 = Sha(client), size = client.Length } },
            assetIndex = new { id = "100", url = "https://files.example/index", sha1 = Sha(index), size = index.Length }
        });
        var requests = new MemoryServer(new Dictionary<string, byte[]>
        {
            ["https://files.example/client"] = client, ["https://files.example/index"] = index,
            ["https://files.example/meta"] = metadata,
            [$"https://resources.download.minecraft.net/{assetHash[..2]}/{assetHash}"] = asset
        });
        using var http = new HttpClient(requests);
        var installer = new VanillaInstaller(new VerifiedDownloader(http, NullLogger<VerifiedDownloader>.Instance), NullLogger<VanillaInstaller>.Instance);
        var version = new VersionEntry("1.100", "release", "https://files.example/meta", Sha(metadata), DateTimeOffset.UtcNow);
        var progress = new CapturedProgress();
        await installer.InstallAsync(directory.Path, version, progress, token);
        Assert.True(installer.IsInstalled(directory.Path, version.Id));
        Assert.Equal(100, progress.Last!.Percent);
        Assert.Equal(client, await File.ReadAllBytesAsync(InstallPaths.ClientFile(directory.Path, version.Id), token));
        var sharedAsset = Path.Combine(directory.Path, "assets", "objects", assetHash[..2], assetHash);
        Assert.True(File.Exists(sharedAsset));
        await installer.DeleteAsync(directory.Path, version.Id, token);
        Assert.False(installer.IsInstalled(directory.Path, version.Id));
        Assert.True(File.Exists(sharedAsset));
    }

    [Fact]
    public async Task InvalidChecksumDoesNotPromotePartialFile()
    {
        using var directory = new TemporaryDirectory();
        using var http = new HttpClient(new MemoryServer(new Dictionary<string, byte[]> { ["https://files.example/test"] = [1, 2, 3] }));
        var download = new VerifiedDownloader(http, NullLogger<VerifiedDownloader>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => download.FetchAsync(new Artifact("https://files.example/test", Sha([1, 2, 4]), 3, null),
            Path.Combine(directory.Path, "file.jar"), null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancelledTransferDoesNotLeaveTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        using var http = new HttpClient(new MemoryServer(new Dictionary<string, byte[]> { ["https://files.example/test"] = [1, 2, 3] }));
        using var cancel = new CancellationTokenSource();
        var download = new VerifiedDownloader(http, NullLogger<VerifiedDownloader>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.FetchAsync(new Artifact("https://files.example/test", Sha([1, 2, 3]), 3, null),
            Path.Combine(directory.Path, "file.jar"), _ => cancel.Cancel(), cancel.Token));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(LoaderKind.Fabric, "https://meta.fabricmc.net/v2/versions/loader", "https://maven.fabricmc.net")]
    [InlineData(LoaderKind.Quilt, "https://meta.quiltmc.org/v3/versions/loader", "https://maven.quiltmc.org")]
    public async Task LoaderInheritsClientArgumentsAndCanBeUsedAfterRemovingBase(LoaderKind kind, string api, string maven)
    {
        using var directory = new TemporaryDirectory();
        var token = TestContext.Current.CancellationToken;
        var client = Encoding.UTF8.GetBytes("vanilla-client-fixture");
        var loaderJar = Encoding.UTF8.GetBytes("loader-jar-fixture");
        var library = "app.fixture:loader:0.10";
        var relative = LoaderCatalog.LibraryRelativePath(library);
        var baseVersion = new VersionEntry("1.100", "release", "https://files.example/base", null, DateTimeOffset.UtcNow);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = baseVersion.Id, type = "release", mainClass = "game.Main",
            arguments = new { game = new[] { "--username", "${auth_player_name}" }, jvm = Array.Empty<string>() },
            downloads = new { client = new { url = "https://files.example/client", sha1 = Sha(client), size = client.Length } },
            libraries = Array.Empty<object>()
        });
        var loaderMetadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "loader-profile", inheritsFrom = baseVersion.Id, mainClass = "loader.KnotClient",
            arguments = new { game = new[] { "--test" } },
            libraries = new[] { new { name = library, url = maven } }
        });
        using var http = new HttpClient(new MemoryServer(new Dictionary<string, byte[]>
        {
            [$"{api}/1.100"] = Encoding.UTF8.GetBytes("""[{"loader":{"version":"0.10","stable":true}}]"""),
            [$"{api}/1.100/0.10/profile/json"] = loaderMetadata,
            ["https://files.example/base"] = metadata,
            ["https://files.example/client"] = client,
            [$"{maven}/{relative}"] = loaderJar,
            [$"{maven}/{relative}.sha1"] = Encoding.UTF8.GetBytes(Sha(loaderJar))
        }));
        var downloader = new VerifiedDownloader(http, NullLogger<VerifiedDownloader>.Instance);
        var installer = new VanillaInstaller(downloader, NullLogger<VanillaInstaller>.Instance);
        var catalog = new LoaderCatalog(http);
        var release = Assert.Single(await catalog.FindAsync(kind, baseVersion.Id, token));
        var recipe = new LoaderRecipe(baseVersion, kind, release);
        var loaderInstaller = new LoaderInstaller(catalog, installer, downloader, NullLogger<LoaderInstaller>.Instance);
        var progress = new CapturedProgress();
        await loaderInstaller.InstallAsync(directory.Path, recipe, progress, token);
        await installer.DeleteAsync(directory.Path, baseVersion.Id, token);
        Assert.True(installer.IsInstalled(directory.Path, recipe.Id));
        Assert.Equal(100, progress.Last!.Percent);
        var combined = await VanillaInstaller.ReadAsync<VersionMetadata>(InstallPaths.MetadataFile(directory.Path, recipe.Id), token);
        Assert.Equal("loader.KnotClient", combined.MainClass);
        Assert.Equal(new[] { "--username", "${auth_player_name}", "--test" }, combined.Arguments!.Game!.Select(x => x.GetString()).ToArray());
        Assert.Equal(Sha(loaderJar), Assert.Single(combined.Libraries).Downloads!.Artifact!.Sha1);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private sealed class MemoryServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }

    private sealed class CapturedProgress : IProgress<InstallProgress>
    {
        public InstallProgress? Last { get; private set; }
        public void Report(InstallProgress value) => Last = value;
    }
}

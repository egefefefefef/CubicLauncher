using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using CubicLauncher.Launcher.Downloads;
using CubicLauncher.Launcher.Installation;
using CubicLauncher.Launcher.Java;
using CubicLauncher.Launcher.ModLoaders;
using CubicLauncher.Launcher.VersionManager;
using Xunit;

namespace CubicLauncher.Tests;

public sealed class JavaAndForgeTests
{
    [Theory]
    [InlineData("windows", "x86_64", "windows-x64")]
    [InlineData("linux", "x86_64", "linux")]
    [InlineData("osx", "x86_64", "mac-os")]
    [InlineData("osx", "aarch64", "mac-os-arm64")]
    public void SelectsMojangRuntimeForEachSupportedTarget(string os, string arch, string expected) =>
        Assert.Equal(expected, JavaProvisioner.PlatformKey(new MinecraftPlatform(os, arch, "")));

    [Fact]
    public void AllowsRuntimeLinksWithinInstallationButRejectsEscapes()
    {
        using var temp = new TemporaryDirectory();
        var parent = Path.Combine(temp.Path, "bin");
        Assert.Equal(Path.Combine(temp.Path, "lib", "java"), JavaProvisioner.RelativeLinkTarget(temp.Path, parent, "../lib/java"));
        Assert.Throws<InvalidDataException>(() => JavaProvisioner.RelativeLinkTarget(temp.Path, parent, "../../outside"));
        Assert.Throws<InvalidDataException>(() => JavaProvisioner.RelativeLinkTarget(temp.Path, parent, "C:\\outside"));
    }

    [Fact]
    public async Task RuntimeManifestWithoutChecksumIsNotDownloaded()
    {
        var platform = JavaProvisioner.PlatformKey(MinecraftPlatform.Current);
        var payload = """
            {"PLATFORM":{"java-runtime-delta":[{"manifest":{"url":"https://piston-meta.mojang.com/manifest","size":12},"version":{"name":"21","released":"2026-01-01T00:00:00Z"}}]}}
            """.Replace("PLATFORM", platform, StringComparison.Ordinal);
        using var temp = new TemporaryDirectory();
        using var http = new HttpClient(new StubHandler(_ => payload));
        var provisioner = new JavaProvisioner(http, new VerifiedDownloader(http, NullLogger<VerifiedDownloader>.Instance),
            new JavaDetector(), NullLogger<JavaProvisioner>.Instance);
        var token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidDataException>(() => provisioner.InstallManagedAsync(temp.Path, new JavaRequirement(21), null, token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Null(await provisioner.FindInstalledAsync(temp.Path, new JavaRequirement(21), token));
    }

    [Fact]
    public async Task ForgeCatalogFiltersMinecraftVersionAndOrdersNumericVersions()
    {
        using var http = new HttpClient(new StubHandler(uri =>
        {
            Assert.Equal("maven.minecraftforge.net", uri.Host);
            return """
                <metadata><versioning><versions><version>1.21.1-52.1.2</version><version>1.20.1-47.4.9</version>
                <version>1.21.1-52.1.10</version></versions></versioning></metadata>
                """;
        }));
        var versions = await new LoaderCatalog(http).FindAsync(LoaderKind.Forge, "1.21.1", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "52.1.10", "52.1.2" }, versions.Select(version => version.Version).ToArray());
        Assert.Equal("https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.10/forge-1.21.1-52.1.10-installer.jar",
            LoaderCatalog.InstallerUri(Recipe(LoaderKind.Forge, "1.21.1", versions[0].Version)).AbsoluteUri);
    }

    [Fact]
    public async Task NeoForgeCatalogMatchesTheMinecraftPatchExactly()
    {
        using var http = new HttpClient(new StubHandler(uri =>
        {
            Assert.Equal("maven.neoforged.net", uri.Host);
            return """
                <metadata><versioning><versions><version>21.1.250-beta</version><version>21.11.20</version>
                <version>21.1.248</version></versions></versioning></metadata>
                """;
        }));
        var versions = await new LoaderCatalog(http).FindAsync(LoaderKind.NeoForge, "1.21.1", TestContext.Current.CancellationToken);
        Assert.Collection(versions, beta => Assert.False(beta.Stable), stable => Assert.Equal("21.1.248", stable.Version));
        Assert.Throws<InvalidDataException>(() => LoaderCatalog.InstallerUri(Recipe(LoaderKind.NeoForge, "1.21.1", "21.11.20")));
        Assert.Equal("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.248/neoforge-21.1.248-installer.jar",
            LoaderCatalog.InstallerUri(Recipe(LoaderKind.NeoForge, "1.21.1", versions[1].Version)).AbsoluteUri);
        Assert.Equal("net/minecraft/client/1.21.1/client-1.21.1-extra.jar", LoaderCatalog.LibraryRelativePath("net.minecraft:client:1.21.1:extra@jar"));
    }

    internal static LoaderRecipe Recipe(LoaderKind kind, string minecraft, string loader) =>
        new(new VersionEntry(minecraft, "release", "", null, DateTimeOffset.UnixEpoch), kind, new LoaderRelease(loader, true));

    private sealed class StubHandler(Func<Uri, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body(request.RequestUri!), Encoding.UTF8, "application/json"), RequestMessage = request });
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using CubicLauncher.Launcher.Downloads;
using CubicLauncher.Launcher.Installation;
using CubicLauncher.Launcher.Java;
using CubicLauncher.Launcher.ModLoaders;
using CubicLauncher.Launcher.VersionManager;
using Xunit;

namespace CubicLauncher.Tests;

public sealed class LiveInstallSmokeTests
{
    public static bool NetworkEnabled => Environment.GetEnvironmentVariable("CUBICLAUNCHER_TEST_LIVE") == "1";

    [Fact(SkipUnless = nameof(NetworkEnabled), Skip = "Downloads official Minecraft, Java and loader packages; enable explicitly.")]
    public async Task InstallsOfficialJavaAndTestsForgeFamilyOnLinux()
    {
        using var temp = new TemporaryDirectory();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var token = TestContext.Current.CancellationToken;
        var downloader = new VerifiedDownloader(http, NullLogger<VerifiedDownloader>.Instance);
        var java = new JavaProvisioner(http, downloader, new JavaDetector(), NullLogger<JavaProvisioner>.Instance);
        var rt = await java.InstallManagedAsync(temp.Path, new JavaRequirement(21, "java-runtime-delta"), null, token);
        Assert.Equal(21, rt.MajorVersion);
        Assert.Equal(rt, await java.FindInstalledAsync(temp.Path, new JavaRequirement(21, "java-runtime-delta"), token));
        if (!OperatingSystem.IsLinux()) return; // Network validation of the loader processors runs on Linux.

        var catalog = new MojangVersionCatalog(http, NullLogger<MojangVersionCatalog>.Instance);
        var version = (await catalog.FetchAsync(token)).Single(v => v.Id == "1.21.1");
        var vanilla = new VanillaInstaller(downloader, NullLogger<VanillaInstaller>.Instance);
        var loaderCatalog = new LoaderCatalog(http);
        var forge = new ForgeInstaller(loaderCatalog, downloader, java, NullLogger<ForgeInstaller>.Instance);
        var installer = new LoaderInstaller(loaderCatalog, vanilla, downloader, NullLogger<LoaderInstaller>.Instance, forge);
        foreach (var kind in new[] { LoaderKind.Forge, LoaderKind.NeoForge })
        {
            var versions = await loaderCatalog.FindAsync(kind, version.Id, token);
            var loader = versions.FirstOrDefault(r => r.Stable) ?? Assert.Single(versions.Take(1));
            var recipe = new LoaderRecipe(version, kind, loader);
            await installer.InstallAsync(temp.Path, recipe, new IgnoreProgress(), token);
            Assert.True(vanilla.IsInstalled(temp.Path, recipe.Id));
            var metadata = await VanillaInstaller.ReadAsync<VersionMetadata>(InstallPaths.MetadataFile(temp.Path, recipe.Id), token);
            Assert.NotEqual("net.minecraft.client.main.Main", metadata.MainClass);
            Assert.Contains(metadata.Arguments!.Game!, arg => arg.ValueKind == System.Text.Json.JsonValueKind.String && arg.GetString() == "--launchTarget");
        }
    }

    private sealed class IgnoreProgress : IProgress<InstallProgress> { public void Report(InstallProgress value) { } }
}

using Microsoft.Extensions.Logging.Abstractions;
using CubicLauncher.Core.Models;
using CubicLauncher.Infrastructure.Storage;
using Xunit;

namespace CubicLauncher.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public async Task CreatesAndPersistsProfilesAndSettings()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new LauncherPaths(temporary.Path);
        var store = new JsonLauncherStateStore(paths, NullLogger<JsonLauncherStateStore>.Instance);
        var initial = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(paths.ConfigurationFile));
        Assert.Equal("CubicLauncher", initial.Settings.LauncherName);
        var profile = new PlayerProfile(Guid.NewGuid(), "Caver") { VersionId = "1.21.4", MaximumRamMb = 8192 };
        await store.SaveAsync(initial with
        {
            Settings = initial.Settings with { Theme = "Light", Language = "Русский" },
            Profiles = [.. initial.Profiles, profile], ActiveProfileId = profile.Id
        }, TestContext.Current.CancellationToken);
        var saved = await new JsonLauncherStateStore(paths, NullLogger<JsonLauncherStateStore>.Instance)
            .LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Light", saved.Settings.Theme);
        Assert.Equal("Русский", saved.Settings.Language);
        Assert.Equal(profile, saved.Profiles.Single(p => p.Id == saved.ActiveProfileId));
        Assert.NotEqual(profile.GameDirectory(paths.DefaultGameDirectory), initial.Profiles[0].GameDirectory(paths.DefaultGameDirectory));
    }

    [Theory]
    [InlineData("VoxelNest", "CubicLauncher")]
    [InlineData("VoxelNest Launcher", "CubicLauncher")]
    [InlineData("My launcher", "My launcher")]
    public async Task RebrandKeepsLegacyProfilesAndCustomName(string previousName, string expectedName)
    {
        using var temporary = new TemporaryDirectory();
        var token = TestContext.Current.CancellationToken;
        var paths = LauncherPaths.InDataDirectory(temporary.Path);
        Assert.Equal(Path.Combine(temporary.Path, "CubicLauncher"), paths.DataDirectory);
        var legacyPaths = new LauncherPaths(Path.Combine(temporary.Path, "VoxelNest"));
        var initial = LauncherState.CreateDefault();
        var legacyStore = new JsonLauncherStateStore(legacyPaths, NullLogger<JsonLauncherStateStore>.Instance);
        await legacyStore.SaveAsync(initial with { Settings = initial.Settings with { LauncherName = previousName } }, token);

        var upgradePaths = LauncherPaths.InDataDirectory(temporary.Path);
        Assert.Equal(legacyPaths, upgradePaths);
        var store = new JsonLauncherStateStore(upgradePaths, NullLogger<JsonLauncherStateStore>.Instance);
        var upgraded = await store.LoadAsync(token);
        Assert.Equal(expectedName, upgraded.Settings.LauncherName);
        Assert.Equal(initial.ActiveProfileId, upgraded.ActiveProfileId);
        Assert.Equal(initial.Profiles[0], upgraded.Profiles[0]);
        Assert.Contains($"\"launcherName\": \"{expectedName}\"", await File.ReadAllTextAsync(legacyPaths.ConfigurationFile, token));
    }

    [Fact]
    public async Task CorruptConfigIsBackedUpBeforeReset()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new LauncherPaths(temporary.Path);
        await File.WriteAllTextAsync(paths.ConfigurationFile, "{truncated…", TestContext.Current.CancellationToken);
        var store = new JsonLauncherStateStore(paths, NullLogger<JsonLauncherStateStore>.Instance);
        var recovered = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Single(recovered.Profiles);
        var backup = Assert.Single(Directory.GetFiles(temporary.Path, "launcher.json.invalid-*").ToList());
        Assert.Equal("{truncated…", await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
    }
}

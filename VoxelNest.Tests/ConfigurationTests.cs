using Microsoft.Extensions.Logging.Abstractions;
using VoxelNest.Core.Models;
using VoxelNest.Infrastructure.Storage;
using Xunit;

namespace VoxelNest.Tests;

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

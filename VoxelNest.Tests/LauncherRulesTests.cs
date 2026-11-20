using System.IO.Compression;
using System.Text.Json;
using VoxelNest.Launcher.GameRunner;
using VoxelNest.Launcher.Installation;
using VoxelNest.Launcher.VersionManager;
using Xunit;

namespace VoxelNest.Tests;

public sealed class LauncherRulesTests
{
    private static readonly MinecraftPlatform Linux = new("linux", "x86_64", "6.8.1");

    [Theory]
    [InlineData("../../file")]
    [InlineData("/tmp/bad")]
    [InlineData("..\\escape")]
    [InlineData("C:\\escape")]
    [InlineData("CON/txt")]
    public void PathResolutionRejectsUnsafePaths(string path)
    {
        using var directory = new TemporaryDirectory();
        Assert.Throws<InvalidDataException>(() => InstallPaths.Under(directory.Path, path));
    }

    [Fact]
    public async Task ArchiveCannotWriteToParentDirectory()
    {
        using var directory = new TemporaryDirectory();
        var archive = Path.Combine(directory.Path, "native.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open()))
            await writer.WriteAsync("unsafe test data");
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeExtractor.ExtractAsync(archive,
            Path.Combine(directory.Path, "natives"), [], TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(directory.Path, "outside.txt")));
    }

    [Fact]
    public void LastMatchingRuleWinsAndFeaturesAreEvaluated()
    {
        PlatformRule[] rules = [new("allow", null, null), new("disallow", new("linux", "x64", "^6\\."), null)];
        Assert.False(RuleEvaluator.Allows(rules, Linux));
        Assert.True(RuleEvaluator.Allows(rules, new MinecraftPlatform("osx", "aarch64", "15.0")));
        using var json = JsonDocument.Parse("""
            ["main", {"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","${resolution_width}"]}]
            """);
        var args = ArgumentBuilder.Read(json.RootElement.EnumerateArray(), Linux,
            new Dictionary<string, bool> { ["has_custom_resolution"] = true });
        Assert.Equal(new[] { "main", "--width", "1280" }, args.Select(a => ArgumentBuilder.Expand(a,
            new Dictionary<string, string> { ["resolution_width"] = "1280" })).ToArray());
        Assert.Equal(new[] { "main" }, ArgumentBuilder.Read(json.RootElement.EnumerateArray(), Linux, new Dictionary<string, bool>()).ToArray());
    }

    [Fact]
    public void TokenizerPreservesQuotedPaths()
    {
        Assert.Equal(new[] { @"-Dpath=C:\test folder\java", "-XX:+UseG1GC" },
            ArgumentBuilder.Tokenize("""-Dpath="C:\test folder\java" -XX:+UseG1GC"""));
        Assert.Throws<FormatException>(() => ArgumentBuilder.Tokenize("--thing=\"missing-close"));
    }
}

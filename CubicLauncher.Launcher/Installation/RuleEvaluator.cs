using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CpuArchitecture = System.Runtime.InteropServices.Architecture;
using CubicLauncher.Launcher.VersionManager;

namespace CubicLauncher.Launcher.Installation;

public sealed record MinecraftPlatform(string Name, string Architecture, string Version)
{
    public static MinecraftPlatform Current { get; } = new(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        RuntimeInformation.ProcessArchitecture switch
        {
            CpuArchitecture.X86 => "x86", CpuArchitecture.X64 => "x86_64",
            CpuArchitecture.Arm64 => "aarch64", _ => "arm"
        }, Environment.OSVersion.Version.ToString());
}

public static class RuleEvaluator
{
    public static bool Allows(IReadOnlyList<PlatformRule>? rules, MinecraftPlatform platform,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Os is { } os &&
                (os.Name is not null && os.Name != platform.Name ||
                 os.Arch is not null && !ArchMatches(os.Arch, platform.Architecture) ||
                 os.Version is not null && !Regex.IsMatch(platform.Version, os.Version, RegexOptions.None, TimeSpan.FromMilliseconds(250))))
                continue;
            if (rule.Features?.Any(pair => (features?.TryGetValue(pair.Key, out var value) == true && value) != pair.Value) == true)
                continue;
            allowed = rule.Action == "allow";
        }
        return allowed;
    }

    private static bool ArchMatches(string rule, string current) => rule == current ||
        (rule, current) is ("x64" or "amd64", "x86_64") or ("arm64", "aarch64");
}

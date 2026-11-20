using System.Globalization;

namespace VoxelNest.Core.Models;

public sealed record LauncherSettings
{
    public string LauncherName { get; init; } = "VoxelNest";
    public string Theme { get; init; } = "Dark";
    public string Language { get; init; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "Русский" : "English";
    public string? BackgroundPath { get; init; }
    public string? GameRoot { get; init; }
    public string? MicrosoftClientId { get; init; }
    public string AfterLaunch { get; init; } = "Keep open";
}

public sealed record PlayerProfile(Guid Id, string DisplayName)
{
    public string? AvatarPath { get; init; }
    public string? VersionId { get; init; }
    public string? JavaPath { get; init; }
    public int MinimumRamMb { get; init; } = 1024;
    public int MaximumRamMb { get; init; } = 4096;
    public string? JvmArguments { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public bool Fullscreen { get; init; }

    public string GameDirectory(string root) => Path.Combine(root, "instances", Id.ToString("N"));
}

public sealed record LauncherState(LauncherSettings Settings, List<PlayerProfile> Profiles, Guid ActiveProfileId)
{
    public static LauncherState CreateDefault()
    {
        var profile = new PlayerProfile(Guid.NewGuid(), "Explorer");
        return new LauncherState(new LauncherSettings(), [profile], profile.Id);
    }
}

namespace CubicLauncher.Infrastructure.Storage;

public sealed record LauncherPaths(string DataDirectory)
{
    public string ConfigurationFile => Path.Combine(DataDirectory, "launcher.json");
    public string DefaultGameDirectory => Path.Combine(DataDirectory, "game");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static LauncherPaths ForCurrentUser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (OperatingSystem.IsMacOS())
            baseDirectory = Path.Combine(home, "Library", "Application Support");
        else if (OperatingSystem.IsLinux())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            baseDirectory = xdg is not null && Path.IsPathFullyQualified(xdg)
                ? xdg : Path.Combine(home, ".local", "share");
        }
        return InDataDirectory(baseDirectory);
    }

    public static LauncherPaths InDataDirectory(string baseDirectory)
    {
        var current = new LauncherPaths(Path.Combine(baseDirectory, "CubicLauncher"));
        var legacy = new LauncherPaths(Path.Combine(baseDirectory, "VoxelNest"));
        // Keep existing profiles, worlds and credentials in place; use the new folder on fresh installs.
        return !Directory.Exists(current.DataDirectory) && Directory.Exists(legacy.DataDirectory) ? legacy : current;
    }
}

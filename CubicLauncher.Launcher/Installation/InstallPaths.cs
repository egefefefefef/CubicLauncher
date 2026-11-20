using System.Runtime.InteropServices;

namespace CubicLauncher.Launcher.Installation;

public static class InstallPaths
{
    public static string PlatformKey => $"{MinecraftPlatform.Current.Name}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

    public static string VersionDirectory(string gameRoot, string id) => Under(Path.Combine(gameRoot, "versions"), Segment(id));
    public static string MetadataFile(string gameRoot, string id) => Path.Combine(VersionDirectory(gameRoot, id), id + ".json");
    public static string ClientFile(string gameRoot, string id) => Path.Combine(VersionDirectory(gameRoot, id), id + ".jar");
    public static string NativeDirectory(string gameRoot, string id) => Path.Combine(VersionDirectory(gameRoot, id), $"natives-{PlatformKey}");
    public static string Receipt(string gameRoot, string id) => Path.Combine(VersionDirectory(gameRoot, id), $"installed-{PlatformKey}.txt");

    public static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 150 || value is "." or ".." ||
            value.EndsWith('.') || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '.')))
            throw new InvalidDataException($"Invalid Minecraft identifier: {value}");
        return value;
    }

    private static bool IsDeviceName(string value)
    {
        var stem = value.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '0' and <= '9' or '¹' or '²' or '³';
    }

    public static string Under(string directory, string path)
    {
        // Use the same path rules on all hosts, even when consuming metadata from a different platform.
        var parts = path.Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || part.Any(char.IsControl) || IsDeviceName(part)))
            throw new InvalidDataException($"Unsafe relative path: {path}");
        var root = Path.GetFullPath(directory);
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException($"Path escapes its directory: {path}");
        return full;
    }
}

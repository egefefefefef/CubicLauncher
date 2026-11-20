using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Win32;

namespace VoxelNest.Infrastructure.Configuration;

public sealed class StartupRegistration
{
    private const string Id = "app.voxelnest.launcher";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public Task<bool> IsEnabledAsync() => Task.Run(() => OperatingSystem.IsWindows() ? GetWindowsEntry() : File.Exists(EntryFile()));

    public Task SetAsync(bool enabled) => Task.Run(async () =>
    {
        if (OperatingSystem.IsWindows()) { SetWindowsEntry(enabled); return; }
        var target = EntryFile();
        if (!enabled) { File.Delete(target); return; }
        var executable = AppHost();
        var content = OperatingSystem.IsMacOS()
            ? new XDocument(new XDeclaration("1.0", "UTF-8", null),
                new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
                new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", Id),
                    new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", executable)),
                    new XElement("key", "RunAtLoad"), new XElement("true"),
                    new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua")))).ToString()
            : $"[Desktop Entry]\nType=Application\nName=VoxelNest Launcher\nExec={DesktopExec(executable)}\nTerminal=false\n";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
    });

    private static string AppHost()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase) ||
            executable.Any(c => char.IsControl(c) || c == '"'))
            throw new InvalidOperationException("Start the packaged VoxelNest executable before enabling login startup.");
        if (OperatingSystem.IsMacOS() && executable.Contains("/AppTranslocation/", StringComparison.Ordinal))
            throw new InvalidOperationException("Move VoxelNest to Applications before enabling login startup.");
        return executable;
    }

    private static string DesktopExec(string path)
    {
        var quoted = path.Replace("\\", "\\\\").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        // The desktop-entry string decoder runs before Exec's quoted-argument decoder.
        return "\"" + quoted.Replace("\\", "\\\\") + "\"";
    }

    private string EntryFile()
    {
        if (OperatingSystem.IsMacOS()) return Path.Combine(_home, "Library", "LaunchAgents", Id + ".plist");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(xdg is not null && Path.IsPathFullyQualified(xdg) ? xdg : Path.Combine(_home, ".config"), "autostart", Id + ".desktop");
    }

    [SupportedOSPlatform("windows")]
    private static bool GetWindowsEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Id) is string;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsEntry(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled) key.DeleteValue(Id, throwOnMissingValue: false);
        else key.SetValue(Id, $"\"{AppHost()}\"", RegistryValueKind.String);
    }
}

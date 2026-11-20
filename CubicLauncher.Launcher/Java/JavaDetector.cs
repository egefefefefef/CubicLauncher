using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CubicLauncher.Launcher.Java;

public sealed record JavaRuntime(string Executable, string Version, int MajorVersion, string? Architecture);

public sealed class JavaNotFoundException(string message) : InvalidOperationException(message);

public sealed partial class JavaDetector
{
    [GeneratedRegex("version\\s+\"(?<version>[^\"]+)\"")]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"os\.arch\s*=\s*(\S+)")]
    private static partial Regex ArchPattern();

    public async Task<JavaRuntime> FindAsync(string? explicitPath, int? required, CancellationToken token, string? architecture = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var manual = await ProbeAsync(explicitPath, token).ConfigureAwait(false);
            if (required is int version && manual.MajorVersion != version)
                throw new InvalidOperationException($"Minecraft requires Java {version}; the selected runtime is Java {manual.MajorVersion}.");
            if (!MatchesArchitecture(manual, architecture)) throw new InvalidOperationException($"The selected Java runtime is {manual.Architecture}; this launcher requires {architecture}.");
            return manual;
        }
        var found = new HashSet<int>();
        foreach (var candidate in Candidates().Distinct())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (OperatingSystem.IsMacOS() && candidate == "/usr/bin/java") continue; // macOS stub can show an installation dialog.
                var runtime = await ProbeAsync(candidate, token).ConfigureAwait(false);
                if (!MatchesArchitecture(runtime, architecture)) continue;
                if (required is null || runtime.MajorVersion == required) return runtime;
                found.Add(runtime.MajorVersion);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        throw new JavaNotFoundException(required is int major
            ? $"Java {major} was not found. Install it or choose its executable in Profiles. Found: {(found.Count == 0 ? "none" : string.Join(", ", found.Order()))}."
            : "Java was not found. Install Java or choose its executable in Profiles.");
    }

    internal static bool MatchesArchitecture(JavaRuntime runtime, string? architecture) => architecture switch
    {
        null => true,
        "aarch64" => runtime.Architecture is "aarch64" or "arm64",
        "x86_64" => runtime.Architecture is "x86_64" or "amd64" or "x64",
        _ => runtime.Architecture == architecture
    };

    public static async Task<JavaRuntime> ProbeAsync(string executable, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Choose a valid absolute path to java (java.exe on Windows).", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("-XshowSettings:properties");
        start.ArgumentList.Add("-version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Java did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
            var match = VersionPattern().Match(output);
            if (process.ExitCode != 0 || !match.Success) throw new InvalidOperationException("The selected executable did not report a Java version.");
            var version = match.Groups["version"].Value;
            var parts = version.Split('.', '-', '+', '_');
            if (!int.TryParse(parts[0] == "1" && parts.Length > 1 ? parts[1] : parts[0], out var major))
                throw new InvalidOperationException("Java returned an unknown version format.");
            var arch = ArchPattern().Match(output);
            return new JavaRuntime(executable, version, major, arch.Success ? arch.Groups[1].Value : null);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Java did not respond to the version check.");
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home)
            yield return Path.Combine(home, "bin", name);
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (Path.IsPathFullyQualified(path.Trim('"'))) yield return Path.Combine(path.Trim('"'), name);

        string[] roots = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft")]
            : OperatingSystem.IsMacOS()
                ? ["/Library/Java/JavaVirtualMachines", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Java", "JavaVirtualMachines")]
                : ["/usr/lib/jvm"];
        foreach (var root in roots)
        {
            string[] directories;
            try { directories = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var directory in directories)
                yield return OperatingSystem.IsMacOS() ? Path.Combine(directory, "Contents", "Home", "bin", name) : Path.Combine(directory, "bin", name);
        }
    }
}

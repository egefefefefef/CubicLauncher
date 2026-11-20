namespace CubicLauncher.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("cubiclauncher-test-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

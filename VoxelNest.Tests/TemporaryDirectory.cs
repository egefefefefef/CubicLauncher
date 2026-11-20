namespace VoxelNest.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("voxelnest-test-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

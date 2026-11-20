using VoxelNest.Core.Models;

namespace VoxelNest.Core.Interfaces;

public interface ILauncherStateStore
{
    Task<LauncherState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LauncherState state, CancellationToken cancellationToken = default);
}

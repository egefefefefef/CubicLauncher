using CubicLauncher.Core.Models;

namespace CubicLauncher.Core.Interfaces;

public interface ILauncherStateStore
{
    Task<LauncherState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LauncherState state, CancellationToken cancellationToken = default);
}

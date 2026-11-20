using System.Text.Json;
using Microsoft.Extensions.Logging;
using CubicLauncher.Core.Interfaces;
using CubicLauncher.Core.Models;

namespace CubicLauncher.Infrastructure.Storage;

public sealed class JsonLauncherStateStore(LauncherPaths paths, ILogger<JsonLauncherStateStore> logger) : ILauncherStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<LauncherState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(paths.ConfigurationFile))
            {
                try
                {
                    LauncherState? state;
                    await using (var stream = File.OpenRead(paths.ConfigurationFile))
                        state = await JsonSerializer.DeserializeAsync<LauncherState>(stream, Options, cancellationToken)
                            .ConfigureAwait(false);
                    if (state is null || state.Settings is null || state.Profiles is not { Count: > 0 } ||
                        state.Profiles.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.DisplayName)) ||
                        state.Profiles.Select(p => p.Id).Distinct().Count() != state.Profiles.Count ||
                        !state.Profiles.Any(p => p.Id == state.ActiveProfileId))
                        throw new JsonException("Invalid launcher configuration.");
                    if (state.Settings.LauncherName is "VoxelNest" or "VoxelNest Launcher")
                    {
                        state = state with { Settings = state.Settings with { LauncherName = LauncherSettings.DefaultLauncherName } };
                        await WriteAsync(state, cancellationToken).ConfigureAwait(false);
                    }
                    return state;
                }
                catch (JsonException ex)
                {
                    var backup = paths.ConfigurationFile + $".invalid-{Guid.NewGuid():N}";
                    File.Move(paths.ConfigurationFile, backup);
                    logger.LogWarning(ex, "Moved invalid configuration to {Backup}", backup);
                }
            }

            var initial = LauncherState.CreateDefault();
            await WriteAsync(initial, cancellationToken).ConfigureAwait(false);
            return initial;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LauncherState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAsync(state, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(LauncherState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var temporary = paths.ConfigurationFile + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, state, Options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, paths.ConfigurationFile, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

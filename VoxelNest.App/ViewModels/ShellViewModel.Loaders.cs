using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoxelNest.Launcher.ModLoaders;

namespace VoxelNest.App.ViewModels;

public sealed partial class ShellViewModel
{
    private CancellationTokenSource? _loaderQuery;
    [ObservableProperty] private bool _isLoadingLoaders;
    [ObservableProperty] private IReadOnlyList<LoaderRelease> _loaderOptions = [];
    [ObservableProperty] private LoaderRelease? _selectedLoader;
    [ObservableProperty] private string? _loaderHint;
    private LoaderKind _selectedLoaderKind;
    public bool CanBrowseLoaders => CanEdit && SelectedVersion is { Recipe: null, Entry.Url.Length: > 0, Entry.Type: "release" or "snapshot" };
    public bool HasLoaderOptions => LoaderOptions.Count > 0;
    public bool CanInstallLoader => CanBrowseLoaders && SelectedLoader is not null;

    partial void OnSelectedLoaderChanged(LoaderRelease? value) => Changed(nameof(CanInstallLoader));
    partial void OnLoaderOptionsChanged(IReadOnlyList<LoaderRelease> value) => Changed(nameof(HasLoaderOptions));

    private void ClearLoaderOptions()
    {
        _loaderQuery?.Cancel();
        LoaderOptions = [];
        SelectedLoader = null;
        LoaderHint = null;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task FindLoadersAsync(string kind)
    {
        if (!CanBrowseLoaders || SelectedVersion is not { } row || !Enum.TryParse<LoaderKind>(kind, out var loader)) return;
        ClearLoaderOptions();
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loaderQuery = token;
        IsLoadingLoaders = true;
        try
        {
            var options = await _loaderCatalog.FindAsync(loader, row.Id, token.Token);
            if (token.IsCancellationRequested || !ReferenceEquals(_loaderQuery, token)) return;
            _selectedLoaderKind = loader;
            LoaderOptions = options;
            SelectedLoader = options.FirstOrDefault(o => o.Stable) ?? options.FirstOrDefault();
            LoaderHint = options.Count > 0 ? L.Format("LoaderFound", loader, row.Id) : L.Format("LoaderNone", loader, row.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (ReferenceEquals(_loaderQuery, token)) ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_loaderQuery, token)) { _loaderQuery = null; IsLoadingLoaders = false; }
        }
    }

    [RelayCommand]
    private async Task InstallLoaderAsync()
    {
        if (!CanInstallLoader || SelectedVersion is not { } row || SelectedLoader is not { } loader) return;
        try
        {
            var recipe = new LoaderRecipe(row.Entry, _selectedLoaderKind, loader);
            var root = GameRoot;
            await InstallAsync(row.Entry, root, recipe);
            if (_installer.IsInstalled(root, recipe.Id)) await SetVersionAsync(recipe.Id);
        }
        catch (Exception ex) { ShowError(ex); }
    }
}

using Microsoft.Extensions.Logging;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CubicLauncher.Launcher.Installation;
using CubicLauncher.Launcher.ModLoaders;
using CubicLauncher.Launcher.VersionManager;

namespace CubicLauncher.App.ViewModels;

public sealed record VersionRow(VersionEntry Entry, bool Installed, LoaderRecipe? Recipe)
{
    public string Id => Entry.Id;
    public string Channel => Entry.Type switch { "old_alpha" => "Alpha", "old_beta" => "Beta", "release" => "Release", "snapshot" => "Snapshot", _ => Entry.Type };
    public string Date => Entry.ReleaseDate;
    public string Platform => Recipe?.Kind.ToString().ToUpperInvariant() ?? "VANILLA";
}

public sealed partial class ShellViewModel
{
    private IReadOnlyList<VersionEntry> _versions = [];
    private IReadOnlyList<VersionRow> _localVersions = [];
    private CancellationTokenSource? _work;
    private VersionEntry? _lastDownload;
    private LoaderRecipe? _lastLoaderRecipe;
    private string? _lastDownloadRoot;
    private bool _downloadCanRetry;
    private bool _isSelectedInstalled;
    [ObservableProperty] private IReadOnlyList<VersionRow> _visibleVersions = [];
    [ObservableProperty] private VersionRow? _selectedVersion;
    [ObservableProperty] private string? _versionSearch;
    [ObservableProperty] private string _versionFilter = "Release";
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string? _catalogIssue;
    [ObservableProperty] private bool _confirmVersionDelete;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private InstallProgress? _currentProgress;
    [ObservableProperty] private string _downloadStateKey = "NoDownloads";
    [ObservableProperty] private bool _isLaunching;
    [ObservableProperty] private bool _isGameRunning;

    public IReadOnlyList<string> VersionFilters { get; } = ["Release", "Snapshot", "Legacy", "Installed", "All"];
    public bool HasCatalogIssue => !string.IsNullOrWhiteSpace(CatalogIssue);
    public bool ShowEmptyCatalog => VisibleVersions.Count == 0 && !IsRefreshing;
    public bool HasVersion => SelectedVersion is not null;
    public bool CanRemoveVersion => CanEdit && SelectedVersion?.Installed == true;
    public bool CanInstallVersion => CanEdit && SelectedVersion is { Installed: false, Entry.Url.Length: > 0 };
    public bool CanRepairVersion => CanEdit && SelectedVersion is { Installed: true } row && (row.Recipe is not null || row.Entry.Url.Length > 0);
    public bool CanChooseVersion => CanEdit && HasVersion;
    public string SelectedVersionLabel => Active.VersionId ?? L["NoVersion"];
    public string InstallationStatus => L[IsGameRunning ? "GameRunning" : IsInstalling ? "Downloading" : Active.VersionId is null
        ? "ChooseToStart" : _isSelectedInstalled ? "Installed" : "NotInstalled"];
    public double DownloadPercent => CurrentProgress?.Percent ?? 0;
    public string DownloadMetrics => CurrentProgress is { } p
        ? L.Format("TransferMetrics", p.FilesDone, p.FilesTotal, FormatBytes(p.BytesRemaining), FormatBytes((long)p.BytesPerSecond)) : "";
    public string DownloadSummary => _lastDownload is null ? L[DownloadStateKey] : $"{_lastLoaderRecipe?.Id ?? _lastDownload.Id}  •  {L[DownloadStateKey]}";
    public bool CanRetryDownload => CanEdit && _downloadCanRetry;
    public bool CanPrimary => CanEdit;
    public bool IsSelectedInstalled => _isSelectedInstalled;
    public string PrimaryLabel => L[IsGameRunning ? "GameRunning" : IsLaunching ? "Launching" : IsInstalling ? "Downloading" :
        Active.VersionId is null ? "ChooseVersion" : _isSelectedInstalled ? "Play" : "Install"];

    partial void OnVisibleVersionsChanged(IReadOnlyList<VersionRow> value) => OnPropertyChanged(nameof(ShowEmptyCatalog));
    partial void OnIsRefreshingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyCatalog));
    partial void OnVersionSearchChanged(string? value) => ApplyVersionFilter();
    partial void OnVersionFilterChanged(string value) => ApplyVersionFilter();
    partial void OnCatalogIssueChanged(string? value) => OnPropertyChanged(nameof(HasCatalogIssue));
    partial void OnSelectedVersionChanged(VersionRow? value)
    {
        ConfirmVersionDelete = false;
        ClearLoaderOptions();
        Changed(nameof(HasVersion), nameof(CanRemoveVersion), nameof(CanInstallVersion), nameof(CanChooseVersion), nameof(CanRepairVersion), nameof(CanBrowseLoaders));
    }
    partial void OnCurrentProgressChanged(InstallProgress? value) => Changed(nameof(DownloadPercent), nameof(DownloadMetrics));
    partial void OnDownloadStateKeyChanged(string value) => OnPropertyChanged(nameof(DownloadSummary));
    partial void OnIsInstallingChanged(bool value) => NotifyWork();
    partial void OnIsLaunchingChanged(bool value) => NotifyWork();
    partial void OnIsGameRunningChanged(bool value) => NotifyWork();

    private void NotifyWork() => Changed(nameof(CanEdit), nameof(CanPrimary), nameof(PrimaryLabel), nameof(InstallationStatus),
        nameof(CanRemoveVersion), nameof(CanInstallVersion), nameof(CanChooseVersion), nameof(CanRetryDownload), nameof(CanSignIn),
        nameof(CanRepairVersion), nameof(CanBrowseLoaders), nameof(CanInstallLoader));

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        if (IsRefreshing || !Ready) return;
        IsRefreshing = true;
        CatalogIssue = null;
        var root = GameRoot;
        try
        {
            var local = await ReadInstalledAsync(root);
            if (GameRoot == root) _localVersions = local;
            ApplyVersionFilter();
            _versions = await _catalog.FetchAsync(_lifetime.Token);
            ApplyVersionFilter();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh official version catalog");
            CatalogIssue = L["CatalogOffline"];
            if (_versions.Count == 0 && _localVersions.Count > 0) VersionFilter = "Installed";
        }
        finally { IsRefreshing = false; }
    }

    private async Task<IReadOnlyList<VersionRow>> ReadInstalledAsync(string root) => await Task.Run(async () =>
    {
        var result = new List<VersionRow>();
        var directory = Path.Combine(root, "versions");
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateDirectories(directory))
        {
            var id = Path.GetFileName(path);
            if (!_installer.IsInstalled(root, id)) continue;
            try
            {
                var metadataPath = InstallPaths.MetadataFile(root, id);
                var metadata = await VanillaInstaller.ReadAsync<VersionMetadata>(metadataPath, _lifetime.Token);
                var recipePath = LoaderInstaller.RecipeFile(root, id);
                var recipe = File.Exists(recipePath) ? await VanillaInstaller.ReadAsync<LoaderRecipe>(recipePath, _lifetime.Token) : null;
                if (recipe is not null && recipe.Id != id) throw new InvalidDataException("Installed loader recipe does not match its directory.");
                result.Add(new VersionRow(new VersionEntry(id, metadata.Type, "", null, recipe?.Base.ReleaseTime ?? File.GetLastWriteTimeUtc(metadataPath)), true, recipe));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            { _logger.LogWarning(ex, "Could not read installed version {Version}", id); }
        }
        return result;
    }, _lifetime.Token);

    private void ApplyVersionFilter()
    {
        var selected = SelectedVersion?.Id;
        var query = VersionSearch?.Trim() ?? "";
        var installed = _localVersions.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var all = _versions.Select(entry => new VersionRow(entry, installed.ContainsKey(entry.Id), null))
            .Concat(_localVersions).DistinctBy(v => v.Id);
        var filter = VersionFilter;
        VisibleVersions = all.Where(v => v.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(v => filter switch
            {
                "Release" => v.Entry.Type == "release", "Snapshot" => v.Entry.Type == "snapshot",
                "Legacy" => v.Entry.Type is "old_alpha" or "old_beta", "Installed" => v.Installed, _ => true
            }).ToArray();
        SelectedVersion = VisibleVersions.FirstOrDefault(v => v.Id == selected);
    }

    private void RefreshInstallationState()
    {
        _isSelectedInstalled = _installer.IsInstalled(GameRoot, Active.VersionId);
        Changed(nameof(SelectedVersionLabel), nameof(IsSelectedInstalled));
        NotifyWork();
        ApplyVersionFilter();
    }

    [RelayCommand]
    private async Task ChooseSelectedVersionAsync()
    {
        if (SelectedVersion is not { } row || !CanEdit) return;
        await SetVersionAsync(row.Id);
    }

    private Task SetVersionAsync(string id)
    {
        var updated = Active with { VersionId = id };
        return SaveAsync(_state with { Profiles = _state.Profiles.Select(p => p.Id == updated.Id ? updated : p).ToList() }, () =>
        {
            LoadActiveProfile();
            Message = L.Format("VersionSelected", id);
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (!CanEdit) return;
        try
        {
            if (Active.VersionId is not { } id) { await NavigateAsync("Versions"); return; }
            var profile = Active.Id;
            var root = GameRoot;
            if (_installer.IsInstalled(root, id)) { await LaunchAsync(id); return; }
            var recipePath = LoaderInstaller.RecipeFile(root, id);
            if (File.Exists(recipePath))
            {
                var recipe = await VanillaInstaller.ReadAsync<LoaderRecipe>(recipePath, _lifetime.Token);
                if (recipe.Id != id) throw new InvalidDataException("The installed loader recipe does not match the selected version.");
                if (Active.Id == profile && Active.VersionId == id && GameRoot == root) await InstallAsync(recipe.Base, root, recipe);
                return;
            }
            if (_versions.Count == 0) await RefreshVersionsAsync();
            if (Active.Id != profile || Active.VersionId != id || GameRoot != root) return;
            if (_versions.FirstOrDefault(v => v.Id == id) is not { } entry) { Message = L["MissingVersionMetadata"]; return; }
            await InstallAsync(entry, root);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    private async Task InstallSelectedVersionAsync()
    {
        if (!CanInstallVersion || SelectedVersion is not { } row) return;
        // Use the active profile so the installed version is immediately launchable from Home.
        await ChooseSelectedVersionAsync();
        if (Active.VersionId == row.Id) await InstallAsync(row.Entry, GameRoot);
    }

    [RelayCommand]
    private async Task RepairVersionAsync()
    {
        if (CanRepairVersion && SelectedVersion is { } row) await InstallAsync(row.Recipe?.Base ?? row.Entry, GameRoot, row.Recipe);
    }

    private async Task InstallAsync(VersionEntry entry, string root, LoaderRecipe? recipe = null)
    {
        if (!CanEdit) return;
        var installationId = recipe?.Id ?? entry.Id;
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _work = token;
        _lastDownload = entry;
        _lastLoaderRecipe = recipe;
        _lastDownloadRoot = root;
        _downloadCanRetry = false;
        DownloadStateKey = "Downloading";
        CurrentProgress = null;
        IsInstalling = true;
        OnPropertyChanged(nameof(DownloadSummary));
        try
        {
            var progress = new Progress<InstallProgress>(p => { if (ReferenceEquals(_work, token) && (CurrentProgress?.FilesDone ?? 0) <= p.FilesDone) CurrentProgress = p; });
            await Task.Run(() => recipe is null ? _installer.InstallAsync(root, entry, progress, token.Token) :
                _loaderInstaller.InstallAsync(root, recipe, progress, token.Token), token.Token);
            DownloadStateKey = "InstallComplete";
            if (CurrentProgress is { } current) CurrentProgress = new InstallProgress(current.FilesTotal, current.FilesTotal, 0, 0);
            _localVersions = await ReadInstalledAsync(root);
            Message = L.Format("InstalledVersion", installationId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DownloadStateKey = "DownloadCancelled";
            _downloadCanRetry = true;
        }
        catch (Exception ex)
        {
            DownloadStateKey = "DownloadFailed";
            _downloadCanRetry = true;
            ShowError(ex);
        }
        finally
        {
            _work = null;
            IsInstalling = false;
            RefreshInstallationState();
        }
    }

    [RelayCommand]
    private void CancelDownload() { if (IsInstalling) _work?.Cancel(); }

    [RelayCommand]
    private async Task RetryDownloadAsync()
    {
        if (CanRetryDownload && _lastDownload is { } version && _lastDownloadRoot is { } root)
            await InstallAsync(version, root, _lastLoaderRecipe);
    }

    [RelayCommand]
    private void AskDeleteVersion() { if (CanRemoveVersion) ConfirmVersionDelete = true; }
    [RelayCommand]
    private void KeepVersion() => ConfirmVersionDelete = false;

    [RelayCommand]
    private async Task DeleteVersionAsync()
    {
        if (!CanRemoveVersion || !ConfirmVersionDelete || SelectedVersion is not { } row) return;
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _work = token;
        NotifyWork();
        try
        {
            await _installer.DeleteAsync(GameRoot, row.Id, token.Token);
            _localVersions = _localVersions.Where(v => v.Id != row.Id).ToArray();
            Message = L.Format("RemovedVersion", row.Id);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _work = null; ConfirmVersionDelete = false; RefreshInstallationState(); }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / 1073741824.0).ToString("0.00", CultureInfo.CurrentCulture) + " GiB",
        >= 1024L * 1024 => (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MiB",
        >= 1024 => (bytes / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " KiB", _ => $"{bytes} B"
    };
}

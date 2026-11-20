using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VoxelNest.App.Services;
using VoxelNest.Core.Interfaces;
using VoxelNest.Core.Models;
using VoxelNest.Infrastructure.Storage;
using VoxelNest.Infrastructure.Configuration;
using VoxelNest.Launcher.Authentication;
using VoxelNest.Launcher.GameRunner;
using VoxelNest.Launcher.Installation;
using VoxelNest.Launcher.Java;
using VoxelNest.Launcher.ModLoaders;
using VoxelNest.Launcher.VersionManager;

namespace VoxelNest.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ILauncherStateStore _store;
    private readonly LauncherPaths _paths;
    private readonly StartupRegistration _startup;
    private readonly ProtectedCredentials _credentials;
    private readonly DesktopFiles _desktop;
    private readonly MojangVersionCatalog _catalog;
    private readonly LoaderCatalog _loaderCatalog;
    private readonly LoaderInstaller _loaderInstaller;
    private readonly VanillaInstaller _installer;
    private readonly MicrosoftSignIn _signIn;
    private readonly JavaDetector _java;
    private readonly MinecraftRunner _runner;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private LauncherState _state = LauncherState.CreateDefault();

    public ShellViewModel(ILauncherStateStore store, LauncherPaths paths, DesktopFiles desktop, MojangVersionCatalog catalog,
        VanillaInstaller installer, MicrosoftSignIn signIn, JavaDetector java, MinecraftRunner runner,
        StartupRegistration startup, ProtectedCredentials credentials, LoaderCatalog loaderCatalog, LoaderInstaller loaderInstaller, ILogger<ShellViewModel> logger)
    {
        (_loaderCatalog, _loaderInstaller) = (loaderCatalog, loaderInstaller);
        (_store, _paths, _desktop, _catalog, _installer, _signIn, _java, _runner, _startup, _credentials, _logger) =
            (store, paths, desktop, catalog, installer, signIn, java, runner, startup, credentials, logger);
    }

    [ObservableProperty] private string _page = "Home";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private Bitmap? _backgroundImage;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private bool _ready;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private UiText _l = new("English");

    public string LauncherName => _state.Settings.LauncherName;
    private PlayerProfile Active => _state.Profiles.First(p => p.Id == _state.ActiveProfileId);
    private string GameRoot => _state.Settings.GameRoot ?? _paths.DefaultGameDirectory;
    public string ActiveName => Active.DisplayName;
    public string Greeting => L.Format("Greeting", ActiveName);
    public string AppVersion => typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string PageTitle => L[Page];
    public bool IsHome => Page == "Home";
    public bool IsVersions => Page == "Versions";
    public bool IsProfiles => Page == "Profiles";
    public bool IsDownloads => Page == "Downloads";
    public bool IsSettings => Page == "Settings";
    public bool IsAbout => Page == "About";
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool CanEdit => Ready && !IsSaving && _work is null && !IsGameRunning;

    public async Task InitializeAsync()
    {
        try
        {
            _state = await _store.LoadAsync(_lifetime.Token);
            LoadSettingsForm();
            LoadActiveProfile();
            await ApplyAppearanceAsync();
            _localVersions = await ReadInstalledAsync(GameRoot);
            RefreshInstallationState();
            Ready = true;
            await Task.WhenAll(RestoreAccountAsync(Active.Id), LoadStartupPreferenceAsync());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
    }

    public void CancelWork() => _lifetime.Cancel();

    [RelayCommand]
    private void ClearMessage() => Message = null;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task NavigateAsync(string page)
    {
        Page = page;
        if (IsVersions && _versions.Count == 0 && Ready) await RefreshVersionsAsync();
    }

    partial void OnPageChanged(string value) => Changed(nameof(PageTitle), nameof(IsHome), nameof(IsVersions), nameof(IsProfiles),
        nameof(IsDownloads), nameof(IsSettings), nameof(IsAbout));
    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnReadyChanged(bool value) => NotifyWork();
    partial void OnIsSavingChanged(bool value) => NotifyWork();

    private async Task SaveAsync(LauncherState next, Func<Task> applied)
    {
        if (!CanEdit) return;
        IsSaving = true;
        try
        {
            await _store.SaveAsync(next, _lifetime.Token);
            _state = next;
            await applied();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { IsSaving = false; }
    }

    private async Task ApplyAppearanceAsync()
    {
        L = new UiText(_state.Settings.Language);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _state.Settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        ReplaceImage(await LoadImageAsync(_state.Settings.BackgroundPath, 1800), BackgroundImage, bitmap => BackgroundImage = bitmap);
        await RefreshAvatarAsync();
        Changed(nameof(LauncherName), nameof(PageTitle), nameof(Greeting), nameof(SelectedVersionLabel), nameof(InstallationStatus),
            nameof(PrimaryLabel), nameof(DownloadSummary), nameof(DownloadMetrics), nameof(AccountStatus), nameof(ProfileDirectory), nameof(ProfileVersion));
    }

    private async Task<Bitmap?> LoadImageAsync(string? path, int width)
    {
        try { return await DesktopFiles.ReadImageAsync(path, width); }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to open custom image"); return null; }
    }

    private async Task RefreshAvatarAsync() => ReplaceImage(await LoadImageAsync(Active.AvatarPath, 160), Avatar, bitmap => Avatar = bitmap);

    private static void ReplaceImage(Bitmap? next, Bitmap? previous, Action<Bitmap?> assign)
    {
        assign(next);
        previous?.Dispose();
    }

    private void ShowError(Exception ex)
    {
        _logger.LogError(ex, "Launcher operation failed");
        Message = ex switch
        {
            HttpRequestException => $"{L["ConnectionError"]} {ex.Message}",
            OperationCanceledException => L["ConnectionTimeout"],
            _ => ex.Message
        };
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }
}

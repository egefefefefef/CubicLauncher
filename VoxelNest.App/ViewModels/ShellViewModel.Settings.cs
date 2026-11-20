using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoxelNest.App.Services;
using VoxelNest.Core.Models;

namespace VoxelNest.App.ViewModels;

public sealed partial class ShellViewModel
{
    [ObservableProperty] private string? _launcherNameInput;
    [ObservableProperty] private string? _themeInput;
    [ObservableProperty] private string? _languageInput;
    [ObservableProperty] private string? _backgroundInput;
    [ObservableProperty] private string? _gameRootInput;
    [ObservableProperty] private string? _microsoftClientInput;
    [ObservableProperty] private string? _afterLaunchInput;
    [ObservableProperty] private bool _startAtLoginInput;
    [ObservableProperty] private bool _canSetStartup;
    private bool _registeredAtLogin;

    public IReadOnlyList<string> Themes { get; } = ["Dark", "Light"];
    public IReadOnlyList<string> Languages { get; } = ["English", "Русский"];
    public IReadOnlyList<string> LaunchBehaviors { get; } = ["Keep open", "Minimize", "Close"];
    public string DefaultGamePath => _paths.DefaultGameDirectory;
    public string LogPath => _paths.LogDirectory;

    private void LoadSettingsForm()
    {
        var settings = _state.Settings;
        LauncherNameInput = settings.LauncherName;
        ThemeInput = Themes.Contains(settings.Theme) ? settings.Theme : Themes[0];
        LanguageInput = Languages.Contains(settings.Language) ? settings.Language : Languages[0];
        BackgroundInput = settings.BackgroundPath;
        GameRootInput = settings.GameRoot;
        MicrosoftClientInput = settings.MicrosoftClientId;
        AfterLaunchInput = LaunchBehaviors.Contains(settings.AfterLaunch) ? settings.AfterLaunch : LaunchBehaviors[0];
    }

    private async Task LoadStartupPreferenceAsync()
    {
        try
        {
            _registeredAtLogin = await _startup.IsEnabledAsync();
            StartAtLoginInput = _registeredAtLogin;
            CanSetStartup = true;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!CanEdit) return;
        var name = LauncherNameInput?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) { Message = L["NameError"]; return; }
        var configuredRoot = Optional(GameRootInput);
        if (configuredRoot is not null && !Path.IsPathFullyQualified(configuredRoot)) { Message = L["GameRootError"]; return; }
        var clientId = Optional(MicrosoftClientInput);
        if (clientId is not null && !Guid.TryParse(clientId, out _)) { Message = L["ClientIdError"]; return; }
        var previousRoot = GameRoot;
        var next = new LauncherSettings
        {
            LauncherName = name, Theme = ThemeInput ?? "Dark", Language = LanguageInput ?? "English",
            BackgroundPath = Optional(BackgroundInput), GameRoot = configuredRoot, MicrosoftClientId = clientId,
            AfterLaunch = AfterLaunchInput ?? "Keep open"
        };
        await SaveAsync(_state with { Settings = next }, async () =>
        {
            await ApplyAppearanceAsync();
            if (previousRoot != GameRoot)
            {
                _lastDownload = null;
                _lastLoaderRecipe = null;
                _lastDownloadRoot = null;
                _downloadCanRetry = false;
                DownloadStateKey = "NoDownloads";
                CurrentProgress = null;
            }
            _localVersions = await ReadInstalledAsync(GameRoot);
            RefreshInstallationState();
            if (CanSetStartup && _registeredAtLogin != StartAtLoginInput)
            {
                await _startup.SetAsync(StartAtLoginInput);
                _registeredAtLogin = StartAtLoginInput;
            }
            Message = L["SettingsSaved"];
        });
    }

    [RelayCommand]
    private async Task PickBackgroundAsync()
    {
        try
        {
            if (await _desktop.PickImageAsync(L["ChooseBackground"]) is not { } path) return;
            using var test = await DesktopFiles.ReadImageAsync(path, 1800);
            if (test is not null) BackgroundInput = path;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    private void ResetBackground() => BackgroundInput = null;

    [RelayCommand]
    private async Task PickGameRootAsync()
    {
        try { if (await _desktop.PickFolderAsync(L["ChooseFolder"]) is { } path) GameRootInput = path; }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    private void ResetGameRoot() => GameRootInput = null;

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        try { await _desktop.OpenAsync(new Uri("https://github.com/egefefefefef/CubicLauncher")); }
        catch (Exception ex) { ShowError(ex); }
    }
}

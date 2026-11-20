using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CubicLauncher.App.Services;
using CubicLauncher.Core.Models;
using CubicLauncher.Launcher.GameRunner;
using CubicLauncher.Launcher.Installation;
using CubicLauncher.Launcher.Java;
using CubicLauncher.Launcher.VersionManager;

namespace CubicLauncher.App.ViewModels;

public sealed partial class ShellViewModel
{
    [ObservableProperty] private PlayerProfile? _selectedProfile;
    [ObservableProperty] private string? _displayNameInput;
    [ObservableProperty] private string? _avatarInput;
    [ObservableProperty] private string? _javaInput;
    [ObservableProperty] private string? _javaInfo;
    [ObservableProperty] private decimal? _minRamInput = 1024;
    [ObservableProperty] private decimal? _maxRamInput = 4096;
    [ObservableProperty] private decimal? _widthInput = 1280;
    [ObservableProperty] private decimal? _heightInput = 720;
    [ObservableProperty] private string? _jvmInput;
    [ObservableProperty] private bool _fullscreenInput;
    [ObservableProperty] private bool _confirmProfileDelete;

    public IReadOnlyList<PlayerProfile> Profiles => _state.Profiles;
    public string ProfileVersion => SelectedProfile?.VersionId ?? L["NoVersion"];
    public string ProfileDirectory => SelectedProfile?.GameDirectory(GameRoot) ?? "";
    public bool HasProfileSelected => SelectedProfile is not null;
    public bool CanDeleteProfile => Profiles.Count > 1 && SelectedProfile is not null;
    public bool IsSelectedProfileActive => SelectedProfile?.Id == Active.Id;
    public bool CanActivateProfile => HasProfileSelected && !IsSelectedProfileActive;

    private void LoadActiveProfile()
    {
        OnPropertyChanged(nameof(Profiles));
        SelectedProfile = Active;
        NotifyActive();
    }

    partial void OnSelectedProfileChanged(PlayerProfile? value)
    {
        ConfirmProfileDelete = false;
        JavaInfo = null;
        DisplayNameInput = value?.DisplayName;
        AvatarInput = value?.AvatarPath;
        JavaInput = value?.JavaPath;
        MinRamInput = value?.MinimumRamMb;
        MaxRamInput = value?.MaximumRamMb;
        JvmInput = value?.JvmArguments;
        WidthInput = value?.Width;
        HeightInput = value?.Height;
        FullscreenInput = value?.Fullscreen ?? false;
        Changed(nameof(ProfileDirectory), nameof(ProfileVersion), nameof(HasProfileSelected), nameof(CanDeleteProfile), nameof(IsSelectedProfileActive), nameof(CanActivateProfile));
    }

    private void NotifyActive()
    {
        Changed(nameof(ActiveName), nameof(Greeting), nameof(CanDeleteProfile), nameof(CanActivateProfile),
            nameof(IsSelectedProfileActive), nameof(AccountStatus), nameof(HasSession));
        RefreshInstallationState();
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        var profile = new PlayerProfile(Guid.NewGuid(), L["NewProfileName"]);
        await SaveAsync(_state with { Profiles = [.. Profiles, profile], ActiveProfileId = profile.Id }, async () =>
        {
            LoadActiveProfile();
            await RefreshAvatarAsync();
            Message = L["ProfileCreated"];
        });
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (!CanEdit || SelectedProfile is not { } profile) return;
        var name = DisplayNameInput?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) { Message = L["NameError"]; return; }
        if (!ValidNumber(MinRamInput, 256, 65536) || !ValidNumber(MaxRamInput, 256, 65536) || MinRamInput > MaxRamInput)
        { Message = L["MemoryError"]; return; }
        if (!ValidNumber(WidthInput, 320, 8192) || !ValidNumber(HeightInput, 240, 8192)) { Message = L["ResolutionError"]; return; }
        var executable = Optional(JavaInput);
        if (executable is not null && (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))) { Message = L["JavaPathError"]; return; }
        try { _ = ArgumentBuilder.Tokenize(JvmInput); }
        catch (FormatException ex) { ShowError(ex); return; }
        var updated = profile with
        {
            DisplayName = name, AvatarPath = Optional(AvatarInput), JavaPath = executable, MinimumRamMb = (int)MinRamInput!.Value,
            MaximumRamMb = (int)MaxRamInput!.Value, JvmArguments = Optional(JvmInput), Width = (int)WidthInput!.Value,
            Height = (int)HeightInput!.Value, Fullscreen = FullscreenInput
        };
        await SaveAsync(_state with { Profiles = _state.Profiles.Select(p => p.Id == updated.Id ? updated : p).ToList() }, async () =>
        {
            OnPropertyChanged(nameof(Profiles));
            SelectedProfile = updated;
            NotifyActive();
            await RefreshAvatarAsync();
            Message = L["ProfileSaved"];
        });
    }

    [RelayCommand]
    private async Task ActivateProfileAsync()
    {
        if (SelectedProfile is not { } profile || profile.Id == Active.Id) return;
        await SaveAsync(_state with { ActiveProfileId = profile.Id }, async () => { NotifyActive(); await RefreshAvatarAsync(); await RestoreAccountAsync(profile.Id); });
    }

    [RelayCommand]
    private void AskDeleteProfile() => ConfirmProfileDelete = true;
    [RelayCommand]
    private void KeepProfile() => ConfirmProfileDelete = false;

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (!CanDeleteProfile || !ConfirmProfileDelete || SelectedProfile is not { } profile) return;
        var remaining = _state.Profiles.Where(p => p.Id != profile.Id).ToList();
        await SaveAsync(new LauncherState(_state.Settings, remaining, Active.Id == profile.Id ? remaining[0].Id : Active.Id), async () =>
        {
            _sessions.Remove(profile.Id);
            try { await _credentials.DeleteAsync(profile.Id); }
            catch (Exception ex) { _logger.LogWarning("Could not remove a protected session ({ExceptionName})", ex.GetType().Name); }
            LoadActiveProfile();
            await RefreshAvatarAsync();
            await RestoreAccountAsync(Active.Id);
            Message = L["ProfileDeleted"];
        });
    }

    [RelayCommand]
    private async Task PickAvatarAsync()
    {
        try
        {
            if (await _desktop.PickImageAsync(L["ChooseAvatar"]) is not { } path) return;
            using var test = await DesktopFiles.ReadImageAsync(path, 160);
            if (test is not null) AvatarInput = path;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    private void ResetAvatar() => AvatarInput = null;

    [RelayCommand]
    private async Task PickJavaAsync()
    {
        try { if (await _desktop.PickExecutableAsync(L["ChooseJava"]) is { } path) JavaInput = path; }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    private async Task CheckJavaAsync()
    {
        var selected = SelectedProfile;
        var explicitPath = Optional(JavaInput);
        var root = GameRoot;
        try
        {
            JavaInfo = L["CheckingJava"];
            var token = _lifetime.Token;
            JavaRequirement? requirement = null;
            if (selected?.VersionId is { } version && _installer.IsInstalled(root, version))
            {
                var metadata = await VanillaInstaller.ReadAsync<VersionMetadata>(InstallPaths.MetadataFile(root, version), token);
                requirement = metadata.JavaVersion ?? new JavaRequirement(8);
            }
            JavaRuntime? runtime = null;
            if (explicitPath is null && requirement is not null)
                runtime = await _javaProvisioner.FindInstalledAsync(root, requirement, token);
            runtime ??= await _java.FindAsync(explicitPath, requirement?.MajorVersion, token, MinecraftPlatform.Current.Architecture);
            if (SelectedProfile == selected) JavaInfo = $"Java {runtime.Version} • {runtime.Architecture}\n{runtime.Executable}";
        }
        catch (JavaNotFoundException) when (explicitPath is null)
        {
            if (SelectedProfile == selected) JavaInfo = L["JavaWillInstall"];
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (SelectedProfile == selected) { JavaInfo = null; ShowError(ex); } }
    }

    private static bool ValidNumber(decimal? value, int lower, int upper) => value is decimal number &&
        number >= lower && number <= upper && decimal.Truncate(number) == number;
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

using CubicLauncher.Launcher.Java;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CubicLauncher.Launcher.Authentication;

namespace CubicLauncher.App.ViewModels;

public sealed partial class ShellViewModel
{
    private readonly Dictionary<Guid, MinecraftSession> _sessions = [];
    [ObservableProperty] private DevicePrompt? _signInPrompt;
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private bool _rememberSession;
    [ObservableProperty] private string? _lastGameLog;

    private MinecraftSession? ActiveSession => _sessions.TryGetValue(Active.Id, out var session) &&
        session.ExpiresAt > DateTimeOffset.UtcNow ? session : null;
    public bool HasSession => ActiveSession is not null;
    public bool CanSignIn => CanEdit && !IsSigningIn;
    public bool HasSignInPrompt => SignInPrompt is not null;
    public string AccountStatus => ActiveSession is { } session ? L.Format("SignedInAs", session.Name) : L["NotSignedIn"];
    public string DeviceCode => SignInPrompt?.Code ?? "";
    public string DeviceUrl => SignInPrompt?.VerificationUri.AbsoluteUri ?? "";
    public bool HasGameLog => !string.IsNullOrEmpty(LastGameLog);
    partial void OnSignInPromptChanged(DevicePrompt? value) => Changed(nameof(DeviceCode), nameof(DeviceUrl), nameof(HasSignInPrompt));
    partial void OnIsSigningInChanged(bool value) => NotifyWork();
    partial void OnLastGameLogChanged(string? value) => OnPropertyChanged(nameof(HasGameLog));

    private async Task RestoreAccountAsync(Guid profile)
    {
        if (_sessions.ContainsKey(profile)) return;
        try
        {
            var session = await _credentials.ReadAsync<MinecraftSession>(profile);
            if (!_sessions.ContainsKey(profile) && !_lifetime.IsCancellationRequested && session?.ExpiresAt > DateTimeOffset.UtcNow)
                _sessions[profile] = session;
            if (Active.Id == profile) Changed(nameof(HasSession), nameof(AccountStatus));
        }
        catch (Exception ex) { _logger.LogDebug("Protected credentials unavailable ({ExceptionName})", ex.GetType().Name); }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (!CanSignIn) return;
        var configured = _state.Settings.MicrosoftClientId
            ?? Environment.GetEnvironmentVariable("CUBICLAUNCHER_MICROSOFT_CLIENT_ID")
            ?? Environment.GetEnvironmentVariable("VOXELNEST_MICROSOFT_CLIENT_ID");
        if (!Guid.TryParse(configured, out var id)) { Message = L["ClientIdRequired"]; return; }
        var profileId = Active.Id;
        var remember = RememberSession;
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _work = token;
        IsSigningIn = true;
        try
        {
            _sessions[profileId] = await _signIn.SignInAsync(id,
                new Progress<DevicePrompt>(prompt => { if (ReferenceEquals(_work, token) && !token.IsCancellationRequested) SignInPrompt = prompt; }), token.Token);
            Message = AccountStatus;
            try
            {
                if (remember) await _credentials.WriteAsync(profileId, _sessions[profileId]);
                else await _credentials.DeleteAsync(profileId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not update the OS credential store ({ExceptionName})", ex.GetType().Name);
                if (remember) Message = L["VaultUnavailable"];
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _work = null;
            SignInPrompt = null;
            IsSigningIn = false;
            Changed(nameof(HasSession), nameof(AccountStatus));
        }
    }

    [RelayCommand]
    private void CancelSignIn() { if (IsSigningIn) _work?.Cancel(); }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (!CanSignIn) return;
        var profile = Active.Id;
        _sessions.Remove(profile);
        Changed(nameof(HasSession), nameof(AccountStatus));
        try { await _credentials.DeleteAsync(profile); }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not clear the OS credential store ({ExceptionName})", ex.GetType().Name);
            Message = L["VaultDeleteError"];
        }
    }

    [RelayCommand]
    private async Task OpenSignInAsync()
    {
        try { if (SignInPrompt is { } prompt) await _desktop.OpenAsync(prompt.VerificationUri); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task LaunchAsync(string version)
    {
        if (ActiveSession is not { } session)
        {
            Changed(nameof(HasSession), nameof(AccountStatus));
            Page = "Profiles";
            Message = L["LoginToPlay"];
            return;
        }
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _work = token;
        IsLaunching = true;
        try
        {
            var profile = Active;
            var root = GameRoot;
            var started = new Progress<int>(_ =>
            {
                if (!ReferenceEquals(_work, token)) return;
                Message = null;
                IsLaunching = false;
                IsGameRunning = true;
                _desktop.ApplyLaunchBehavior(_state.Settings.AfterLaunch);
            });
            var javaProgress = new Progress<JavaProgress>(p =>
            {
                if (!ReferenceEquals(_work, token) || !IsLaunching || token.IsCancellationRequested) return;
                Message = p.FilesTotal > 0 ? L.Format("JavaInstallFiles", p.FilesReady, p.FilesTotal) : L["JavaInstallProgress"];
            });
            var result = await Task.Run(() => _runner.RunAsync(root, version, profile, session, started, token.Token, javaProgress), token.Token);
            LastGameLog = result.LogFile;
            Message = result.ExitCode == 0 ? L["GameExited"] : L.Format("GameError", result.ExitCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _work = null; IsLaunching = false; IsGameRunning = false; NotifyWork(); _desktop.CompleteGame(); }
    }

    [RelayCommand]
    private void CancelLaunch()
    {
        if (!IsLaunching) return;
        _work?.Cancel();
        Message = L["LaunchCancelled"];
    }

    [RelayCommand]
    private async Task OpenGameLogAsync()
    {
        try { if (LastGameLog is { } path) await _desktop.OpenFileAsync(path); }
        catch (Exception ex) { ShowError(ex); }
    }
}

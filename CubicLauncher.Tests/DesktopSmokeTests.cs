using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CubicLauncher.App.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(CubicLauncher.Tests.DesktopTestApplication))]

namespace CubicLauncher.Tests;

public sealed class DesktopSmokeTests
{
    [AvaloniaFact]
    public void MainWindowLoadsAllSixViewsAndCanSwitchThemes()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var controls = window.GetVisualDescendants().ToArray();
            Assert.Single(controls.OfType<HomeView>());
            Assert.Single(controls.OfType<VersionsView>());
            Assert.Single(controls.OfType<ProfilesView>());
            Assert.Single(controls.OfType<DownloadsView>());
            Assert.Single(controls.OfType<SettingsView>());
            Assert.Single(controls.OfType<AboutView>());
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
        }
        finally { window.Close(); }
    }
}

public sealed class DesktopTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<CubicLauncher.App.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions()).WithInterFont();
}

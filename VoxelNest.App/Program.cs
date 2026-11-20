using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoxelNest.App.Services;
using VoxelNest.App.ViewModels;
using VoxelNest.Core.Interfaces;
using VoxelNest.Infrastructure.Logging;
using VoxelNest.Infrastructure.Configuration;
using VoxelNest.Infrastructure.Storage;
using VoxelNest.Launcher.Downloads;
using VoxelNest.Launcher.Authentication;
using VoxelNest.Launcher.Java;
using VoxelNest.Launcher.GameRunner;
using VoxelNest.Launcher.Installation;
using VoxelNest.Launcher.VersionManager;
using VoxelNest.Launcher.ModLoaders;

namespace VoxelNest.App;

internal static class Program
{
    internal static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(LauncherPaths.ForCurrentUser());
        collection.AddSingleton<ILoggerProvider, FileLoggerProvider>();
        collection.AddLogging();
        collection.AddSingleton<ILauncherStateStore, JsonLauncherStateStore>();
        collection.AddSingleton<DesktopFiles>();
        collection.AddSingleton<StartupRegistration>();
        collection.AddSingleton<ProtectedCredentials>();
        collection.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
        collection.AddSingleton<VerifiedDownloader>();
        collection.AddSingleton<MojangVersionCatalog>();
        collection.AddSingleton<VanillaInstaller>();
        collection.AddSingleton<LoaderCatalog>();
        collection.AddSingleton<LoaderInstaller>();
        collection.AddSingleton<MicrosoftSignIn>();
        collection.AddSingleton<JavaDetector>();
        collection.AddSingleton<MinecraftRunner>();
        collection.AddSingleton<ShellViewModel>();
        using var provider = collection.BuildServiceProvider();
        Services = provider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Desktop");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled application exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { logger.LogCritical(ex, "VoxelNest failed"); throw; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VoxelNest.App.Services;
using VoxelNest.App.ViewModels;
using VoxelNest.App.Views;

namespace VoxelNest.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Program.Services.GetRequiredService<ShellViewModel>();
            var window = new MainWindow { DataContext = vm };
            var files = Program.Services.GetRequiredService<DesktopFiles>();
            files.Owner = window;
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await vm.InitializeAsync();
            window.Closing += (_, e) =>
            {
                if (!vm.IsGameRunning || e.IsProgrammatic) return;
                e.Cancel = true;
                files.HideForGame();
            };
            window.Closed += (_, _) => vm.CancelWork();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

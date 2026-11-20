using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace VoxelNest.App.Services;

public sealed class DesktopFiles
{
    public TopLevel? Owner { get; set; }
    public bool HasDeferredClose { get; private set; }

    public async Task<string?> PickImageAsync(string title)
    {
        if (Owner?.StorageProvider is not { CanOpen: true } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner?.StorageProvider is not { CanPickFolder: true } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickExecutableAsync(string title)
    {
        if (Owner?.StorageProvider is not { CanOpen: true } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task OpenAsync(Uri uri)
    {
        if (Owner is null || !await Owner.Launcher.LaunchUriAsync(uri))
            throw new InvalidOperationException("The operating system could not open the requested location.");
    }

    public async Task OpenFileAsync(string path)
    {
        if (Owner is null || !await Owner.Launcher.LaunchFileInfoAsync(new FileInfo(path)))
            throw new InvalidOperationException($"Could not open {path}.");
    }

    public void ApplyLaunchBehavior(string behavior)
    {
        if (Owner is not Window window) return;
        if (behavior == "Minimize") window.WindowState = WindowState.Minimized;
        else if (behavior == "Close") HideForGame();
    }

    public void HideForGame()
    {
        if (Owner is not Window window) return;
        HasDeferredClose = true;
        window.Hide();
    }

    public void CompleteGame()
    {
        if (!HasDeferredClose || Owner is not Window window) return;
        HasDeferredClose = false;
        window.Close();
    }

    public static Task<Bitmap?> ReadImageAsync(string? path, int width) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 25 * 1024 * 1024) throw new IOException("Image files must be 25 MB or smaller.");
        return Bitmap.DecodeToWidth(stream, width);
    });
}

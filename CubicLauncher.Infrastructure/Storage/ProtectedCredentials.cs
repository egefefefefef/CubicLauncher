using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Identity.Client.Extensions.Msal;
using CredentialStorage = Microsoft.Identity.Client.Extensions.Msal.Storage;

namespace CubicLauncher.Infrastructure.Storage;

public sealed class ProtectedCredentials(LauncherPaths paths)
{
    private CredentialStorage ForProfile(Guid profile)
    {
        var id = profile.ToString("N");
        // Preserve the original keychain and keyring lookup IDs for existing sessions.
        var settings = new StorageCreationPropertiesBuilder($"{id}.bin", Path.Combine(paths.DataDirectory, "credentials"))
            .WithMacKeyChain("app.voxelnest.launcher", id)
            .WithLinuxKeyring("app.voxelnest.launcher", "default", "CubicLauncher Minecraft session",
                new KeyValuePair<string, string>("app", "VoxelNest"), new KeyValuePair<string, string>("profile", id))
            .Build();
        // Never enable MSAL's optional unprotected-file fallback.
        return CredentialStorage.Create(settings);
    }

    public Task<T?> ReadAsync<T>(Guid profile) where T : class => Task.Run(() =>
    {
        var bytes = ForProfile(profile).ReadData();
        try { return bytes.Length == 0 ? null : JsonSerializer.Deserialize<T>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    });

    public Task WriteAsync<T>(Guid profile, T entry) where T : class => Task.Run(() =>
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        try { ForProfile(profile).WriteData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    });

    public Task DeleteAsync(Guid profile) => Task.Run(() => ForProfile(profile).Clear());
}

using System.Net.Http.Json;
using System.Text.Json;

namespace VoxelNest.Launcher.Authentication;

public sealed record DevicePrompt(string Code, Uri VerificationUri, DateTimeOffset ExpiresAt);
public sealed class MinecraftSession(string id, string name, string accessToken, DateTimeOffset expiresAt)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string AccessToken { get; } = accessToken;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}

public sealed class MicrosoftSignIn(HttpClient http)
{
    private static readonly JsonSerializerOptions XboxJson = new();
    private const string OAuth = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private const string Minecraft = "https://api.minecraftservices.com/";

    public async Task<MinecraftSession> SignInAsync(Guid clientId, IProgress<DevicePrompt> progress, CancellationToken token)
    {
        var configuration = new Dictionary<string, string> { ["client_id"] = clientId.ToString(), ["scope"] = "XboxLive.signin" };
        using var codeResponse = await http.PostAsync(OAuth + "devicecode", new FormUrlEncodedContent(configuration), token).ConfigureAwait(false);
        using var device = await ReadAsync(codeResponse, "Microsoft device authorization", token).ConfigureAwait(false);
        var root = device.RootElement;
        var deviceCode = Require(root, "device_code");
        var verification = new Uri(Require(root, "verification_uri"));
        if (verification.Scheme != Uri.UriSchemeHttps || verification.Host is not ("microsoft.com" or "www.microsoft.com" or "login.microsoftonline.com"))
            throw new InvalidDataException("Microsoft returned an unexpected device authorization URL.");
        var expires = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32());
        var wait = root.TryGetProperty("interval", out var interval) ? Math.Max(1, interval.GetInt32()) : 5;
        progress.Report(new DevicePrompt(Require(root, "user_code"), verification, expires));
        using var polling = CancellationTokenSource.CreateLinkedTokenSource(token);
        polling.CancelAfter(expires - DateTimeOffset.UtcNow > TimeSpan.Zero ? expires - DateTimeOffset.UtcNow : TimeSpan.Zero);
        string microsoftToken;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), polling.Token).ConfigureAwait(false);
                using var response = await http.PostAsync(OAuth + "token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId.ToString(), ["device_code"] = deviceCode
                }), polling.Token).ConfigureAwait(false);
                await using var stream = await response.Content.ReadAsStreamAsync(polling.Token).ConfigureAwait(false);
                using var result = await JsonDocument.ParseAsync(stream, cancellationToken: polling.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) { microsoftToken = Require(result.RootElement, "access_token"); break; }
                switch (Require(result.RootElement, "error"))
                {
                    case "authorization_pending": continue;
                    case "slow_down": wait += 5; continue;
                    case "authorization_declined": throw new InvalidOperationException("Microsoft sign-in was declined.");
                    default: throw new InvalidOperationException("Microsoft sign-in could not be completed. Check the client registration and try again.");
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && polling.IsCancellationRequested)
        {
            throw new TimeoutException("The Microsoft device code expired. Start sign-in again.");
        }

        using var xboxResponse = await http.PostAsJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftToken },
            RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
        }, XboxJson, token).ConfigureAwait(false);
        using var xbox = await ReadAsync(xboxResponse, "Xbox Live authentication", token).ConfigureAwait(false);
        using var xstsResponse = await http.PostAsJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { Require(xbox.RootElement, "Token") } },
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
        }, XboxJson, token).ConfigureAwait(false);
        using var xsts = await ReadAsync(xstsResponse, "Xbox XSTS (check Xbox profile and family settings)", token).ConfigureAwait(false);
        var xui = xsts.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0];
        var identityToken = $"XBL3.0 x={Require(xui, "uhs")};{Require(xsts.RootElement, "Token")}";
        using var mcResponse = await http.PostAsJsonAsync(Minecraft + "authentication/login_with_xbox", new { identityToken }, token).ConfigureAwait(false);
        using var mc = await ReadAsync(mcResponse, "Minecraft authentication", token).ConfigureAwait(false);
        var access = Require(mc.RootElement, "access_token");
        using var entitlements = await GetAuthorizedAsync("entitlements/mcstore", access, "Minecraft ownership verification", token).ConfigureAwait(false);
        if (!entitlements.RootElement.GetProperty("items").EnumerateArray().Any(item =>
            item.TryGetProperty("name", out var name) && name.GetString() is "game_minecraft" or "product_minecraft" or "game_minecraft_java" or "product_minecraft_java"))
            throw new InvalidOperationException("This Microsoft account has no Minecraft Java Edition entitlement.");
        using var profile = await GetAuthorizedAsync("minecraft/profile", access, "Minecraft Java profile (check ownership)", token).ConfigureAwait(false);
        // Obtain the real Java profile independently of the launcher display name.
        var profileId = Require(profile.RootElement, "id");
        if (!Guid.TryParseExact(profileId, "N", out _)) throw new InvalidDataException("Invalid Minecraft profile ID.");
        return new MinecraftSession(profileId, Require(profile.RootElement, "name"), access,
            DateTimeOffset.UtcNow.AddSeconds(mc.RootElement.GetProperty("expires_in").GetInt32()));
    }

    private async Task<JsonDocument> GetAuthorizedAsync(string path, string token, string operation, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Minecraft + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        return await ReadAsync(response, operation, cancellation).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, string operation, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation} failed (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    private static string Require(JsonElement element, string property) => element.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"Authentication response is missing {property}.");
}

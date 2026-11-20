using System.Net;
using System.Text;
using CubicLauncher.Launcher.Authentication;
using Xunit;

namespace CubicLauncher.Tests;

public sealed class AuthenticationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChecksMinecraftOwnershipBeforeReturningRealProfile(bool hasLicense)
    {
        using var endpoints = new FakeMicrosoft(hasLicense);
        using var http = new HttpClient(endpoints);
        var token = TestContext.Current.CancellationToken;
        var prompt = new PromptReceiver();
        var signIn = new MicrosoftSignIn(http);
        if (hasLicense)
        {
            var session = await signIn.SignInAsync(Guid.NewGuid(), prompt, token);
            Assert.Equal("MinecraftName", session.Name);
            Assert.True(endpoints.RequestedProfile);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.SignInAsync(Guid.NewGuid(), prompt, token));
            Assert.Contains("entitlement", error.Message, StringComparison.Ordinal);
            Assert.False(endpoints.RequestedProfile);
        }
        Assert.Equal("ABC-DEF", prompt.Last?.Code);
    }

    private sealed class PromptReceiver : IProgress<DevicePrompt>
    {
        public DevicePrompt? Last { get; private set; }
        public void Report(DevicePrompt value) => Last = value;
    }

    private sealed class FakeMicrosoft(bool hasLicense) : HttpMessageHandler
    {
        public bool RequestedProfile { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/user/authenticate" or "/xsts/authorize")
                Assert.Contains("\"Properties\"", await request.Content!.ReadAsStringAsync(token), StringComparison.Ordinal);
            if (path is "/entitlements/mcstore" or "/minecraft/profile")
                Assert.Equal("minecraft-access-token", request.Headers.Authorization?.Parameter);
            if (path == "/minecraft/profile") RequestedProfile = true;
            var payload = path switch
            {
                "/consumers/oauth2/v2.0/devicecode" => """{"device_code":"device-secret","user_code":"ABC-DEF","verification_uri":"https://www.microsoft.com/link","expires_in":30,"interval":1}""",
                "/consumers/oauth2/v2.0/token" => """{"access_token":"microsoft-access-token"}""",
                "/user/authenticate" => """{"Token":"xbox-token"}""",
                "/xsts/authorize" => """{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"profile-hash"}]}}""",
                "/authentication/login_with_xbox" => """{"access_token":"minecraft-access-token","expires_in":3600}""",
                "/entitlements/mcstore" => hasLicense ? """{"items":[{"name":"game_minecraft"}]}""" : """{"items":[]}""",
                "/minecraft/profile" => """{"id":"e0974101fd09473ca94b0ceefb817419","name":"MinecraftName"}""",
                _ => throw new InvalidOperationException($"Unexpected test request: {path}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }
}

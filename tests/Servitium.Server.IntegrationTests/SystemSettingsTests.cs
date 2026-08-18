using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;

using Servitium.Contracts.Distribution;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Settings;
using Servitium.Server.Hosting;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class SystemSettingsTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1130")]
    [Trait("TestId", "SEC-350")]
    [Trait("TestId", "SEC-351")]
    public async Task 安全设置_拒绝不安全暴露并在重启投影中恢复可信代理配置()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "servitium-settings-" + Guid.NewGuid().ToString("N"));
        using WebApplicationFactory<Program> baseFactory = new();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Servitium:DataRoot", dataRoot));
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        try
        {
            string csrf = await GetCsrfAsync(client).ConfigureAwait(true);
            client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
            using HttpResponseMessage initialize = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/initialize", UriKind.Relative),
                new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, initialize.StatusCode);
            using HttpResponseMessage login = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            using HttpResponseMessage unsafeExposure = await client.PatchAsJsonAsync(
                new Uri("/api/v1/settings/security", UriKind.Relative),
                new UpdateSettingsGroupRequest(
                    new Dictionary<string, string?>(StringComparer.Ordinal) { ["listenAddress"] = "0.0.0.0" },
                    0)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, unsafeExposure.StatusCode);
            Assert.Contains("tls-required", await unsafeExposure.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);

            using HttpResponseMessage incompleteProxy = await client.PatchAsJsonAsync(
                new Uri("/api/v1/settings/security", UriKind.Relative),
                new UpdateSettingsGroupRequest(
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["listenAddress"] = "0.0.0.0",
                        ["trustedProxyMode"] = "true",
                    },
                    0)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, incompleteProxy.StatusCode);

            Dictionary<string, string?> values = new(StringComparer.Ordinal)
            {
                ["listenAddress"] = "0.0.0.0",
                ["listenPort"] = "6123",
                ["pathBase"] = "/servitium",
                ["publicBaseUrl"] = "https://panel.example.test/servitium",
                ["corsOrigins"] = "https://desktop.example.test",
                ["trustedProxyMode"] = "true",
                ["trustedProxyNetworks"] = "10.20.0.0/16",
                ["forwardedForHeader"] = "X-Real-IP",
                ["responseCompressionEnabled"] = "false",
            };
            using HttpResponseMessage savedResponse = await client.PatchAsJsonAsync(
                new Uri("/api/v1/settings/security", UriKind.Relative),
                new UpdateSettingsGroupRequest(values, 0)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
            SettingsGroupDto saved = (await savedResponse.Content.ReadFromJsonAsync<SettingsGroupDto>().ConfigureAwait(true))!;
            Assert.Equal(1, saved.Revision);
            Assert.Equal("restart-required", saved.Effects!["listenPort"]);

            ServerHostOptions projected = new();
            PersistedServerHostSettings.Apply(projected, Path.Combine(dataRoot, "server.db"));
            Assert.Equal("0.0.0.0", projected.ListenAddress);
            Assert.Equal(6123, projected.ListenPort);
            Assert.Equal("/servitium", projected.PathBase);
            Assert.Equal(new Uri("https://panel.example.test/servitium/"), projected.PublicBaseUri);
            Assert.Equal(["https://desktop.example.test"], projected.AllowedOrigins);
            Assert.True(projected.TrustedProxyMode);
            Assert.Equal(["10.20.0.0/16"], projected.TrustedProxyNetworks);
            Assert.Equal("X-Real-IP", projected.ForwardedForHeader);
            Assert.False(projected.ResponseCompressionEnabled);

            using HttpResponseMessage channelResponse = await client.PostAsJsonAsync(
                new Uri("/api/v1/hmcl/channels", UriKind.Relative),
                new CreateHmclChannelRequest("public-base", "Public base", "public")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, channelResponse.StatusCode);
            HmclChannelDto channel = (await channelResponse.Content.ReadFromJsonAsync<HmclChannelDto>().ConfigureAwait(true))!;
            Assert.Equal(
                "https://panel.example.test/servitium/hmcl/public-base/server-manifest.json",
                channel.ManifestUrl?.AbsoluteUri);

            using HttpResponseMessage wildcard = await client.PatchAsJsonAsync(
                new Uri("/api/v1/settings/security", UriKind.Relative),
                new UpdateSettingsGroupRequest(
                    new Dictionary<string, string?>(StringComparer.Ordinal) { ["corsOrigins"] = "https://*.example.test" },
                    saved.Revision)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, wildcard.StatusCode);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        using HttpResponseMessage state = await client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
        string cookie = state.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("servitium.csrf=", StringComparison.Ordinal));
        return cookie["servitium.csrf=".Length..cookie.IndexOf(';', StringComparison.Ordinal)];
    }
}

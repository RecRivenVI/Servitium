using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Settings;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class OidcDiscoveryEndpointTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1150")]
    public async Task Oidc发现测试_校验完整Https文档并拒绝颁发者错配()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "servitium-oidc-test-" + Guid.NewGuid().ToString("N"));
        FakeOutboundHttpClient outbound = new();
        using WebApplicationFactory<Program> baseFactory = new();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("Servitium:DataRoot", dataRoot);
            _ = builder.ConfigureServices(services =>
            {
                _ = services.RemoveAll<IOutboundHttpClient>();
                _ = services.AddSingleton<IOutboundHttpClient>(outbound);
            });
        });
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

            outbound.Document = /*lang=json,strict*/ """
                {
                  "issuer": "https://id.example.test/tenant",
                  "authorization_endpoint": "https://id.example.test/tenant/authorize",
                  "token_endpoint": "https://id.example.test/tenant/token",
                  "jwks_uri": "https://id.example.test/tenant/keys"
                }
                """;
            using HttpResponseMessage accepted = await client.PostAsJsonAsync(
                new Uri("/api/v1/settings/identity/test", UriKind.Relative),
                new TestOidcDiscoveryRequest("https://id.example.test/tenant/")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            SettingsTestResultDto valid = (await accepted.Content.ReadFromJsonAsync<SettingsTestResultDto>().ConfigureAwait(true))!;
            Assert.True(valid.Succeeded);
            Assert.Equal("discovery-valid", valid.Summary);
            Assert.Equal(new Uri("https://id.example.test/tenant/.well-known/openid-configuration"), outbound.LastRequest?.Uri);
            Assert.Equal(1024 * 1024, outbound.LastRequest?.MaximumResponseBytes);

            outbound.Document = outbound.Document.Replace("https://id.example.test/tenant\"", "https://attacker.example/tenant\"", StringComparison.Ordinal);
            using HttpResponseMessage rejected = await client.PostAsJsonAsync(
                new Uri("/api/v1/settings/identity/test", UriKind.Relative),
                new TestOidcDiscoveryRequest("https://id.example.test/tenant")).ConfigureAwait(true);
            SettingsTestResultDto invalid = (await rejected.Content.ReadFromJsonAsync<SettingsTestResultDto>().ConfigureAwait(true))!;
            Assert.False(invalid.Succeeded);
            Assert.Equal("oidc-discovery-failed", invalid.Code);
            Assert.Equal("discovery-invalid", invalid.Summary);

            using HttpResponseMessage insecure = await client.PostAsJsonAsync(
                new Uri("/api/v1/settings/identity/test", UriKind.Relative),
                new TestOidcDiscoveryRequest("http://id.example.test")).ConfigureAwait(true);
            SettingsTestResultDto insecureResult = (await insecure.Content.ReadFromJsonAsync<SettingsTestResultDto>().ConfigureAwait(true))!;
            Assert.False(insecureResult.Succeeded);
            Assert.Equal("discovery-invalid", insecureResult.Summary);
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

    private sealed class FakeOutboundHttpClient : IOutboundHttpClient
    {
        internal string Document { get; set; } = "{}";
        internal OutboundHttpRequest? LastRequest { get; private set; }

        public Task<OutboundHttpResponse> SendAsync(OutboundHttpRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            Stream content = new MemoryStream(Encoding.UTF8.GetBytes(Document), writable: false);
            return Task.FromResult(new OutboundHttpResponse(200, content, request.Uri));
        }
    }
}

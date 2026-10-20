using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Nodes;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class NodeEndpointTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    [Trait("Category", "Integration")]
    [Trait("Category", "Security")]
    [Trait("TestId", "INT-306")]
    public async Task 节点注册令牌拒绝越界有效期而不是静默改写(int lifetimeMinutes)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"servitium-node-token-test-{Guid.NewGuid():N}");
        using WebApplicationFactory<Program> baseFactory = new();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(
            builder => builder.UseSetting("Servitium:DataRoot", dataRoot));
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

        try
        {
            await InitializeAndSignInAsync(client).ConfigureAwait(true);

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                new Uri("/api/v1/nodes/registration-tokens", UriKind.Relative),
                new CreateNodeRegistrationTokenRequest("Synthetic node", lifetimeMinutes)).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            ProblemDetailsDto? problem = await response.Content.ReadFromJsonAsync<ProblemDetailsDto>().ConfigureAwait(true);
            Assert.NotNull(problem);
            Assert.Equal("validation-failed", problem.Code);
            Assert.Contains(nameof(CreateNodeRegistrationTokenRequest.LifetimeMinutes), problem.Errors!.Keys);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-305")]
    public async Task 读取Java列表_使用控制平面镜像且不向节点发送指令()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"servitium-node-endpoint-test-{Guid.NewGuid():N}");
        FakeAgentGateway gateway = new();
        using WebApplicationFactory<Program> baseFactory = new();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("Servitium:DataRoot", dataRoot);
            _ = builder.ConfigureTestServices(services =>
            {
                _ = services.RemoveAll<IAgentGateway>();
                _ = services.AddSingleton<IAgentGateway>(gateway);
            });
        });
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

        try
        {
            using HttpResponseMessage state = await client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
            string csrfCookie = state.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("servitium.csrf=", StringComparison.Ordinal));
            string csrf = csrfCookie["servitium.csrf=".Length..csrfCookie.IndexOf(';', StringComparison.Ordinal)];
            client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);

            using HttpResponseMessage initialize = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/initialize", UriKind.Relative),
                new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Created, initialize.StatusCode);
            using HttpResponseMessage login = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            var nodeId = Guid.NewGuid();
            using HttpResponseMessage response = await client.GetAsync(
                new Uri($"/api/v1/nodes/{nodeId:D}/java-runtimes", UriKind.Relative)).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(gateway.NodeId);
            Assert.Null(gateway.Command);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static async Task InitializeAndSignInAsync(HttpClient client)
    {
        using HttpResponseMessage state = await client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
        string csrfCookie = state.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("servitium.csrf=", StringComparison.Ordinal));
        string csrf = csrfCookie["servitium.csrf=".Length..csrfCookie.IndexOf(';', StringComparison.Ordinal)];
        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        using HttpResponseMessage initialize = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, initialize.StatusCode);
        using HttpResponseMessage login = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private sealed class FakeAgentGateway : IAgentGateway
    {
        internal Guid? NodeId { get; private set; }
        internal AgentCommand? Command { get; private set; }

        public Task<AgentCommandResult> SendAsync(Guid nodeId, AgentCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NodeId = nodeId;
            Command = command;
            return Task.FromResult(new AgentCommandResult(true, JsonSerializer.SerializeToUtf8Bytes(Array.Empty<object>()), null));
        }
    }
}

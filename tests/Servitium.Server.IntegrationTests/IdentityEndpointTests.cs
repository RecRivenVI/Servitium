using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Servitium.Application.Identity;
using Servitium.Application.Results;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Identity;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class IdentityEndpointTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-100")]
    public async Task 首次启动_应用迁移并创建隔离数据库()
    {
        using TestServerContext test = new();
        using HttpResponseMessage response = await test.Client.GetAsync(new Uri("/health/ready", UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(test.DataRoot, "server.db")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-006")]
    public async Task 初始化请求_缺少Csrf令牌时拒绝()
    {
        using TestServerContext test = new();
        using HttpResponseMessage response = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "a-strong-passphrase", "zh-Hans", null)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Equal("csrf-token-missing", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-110")]
    public async Task 初始化登录用户管理和ApiKey_形成持久认证链路()
    {
        using TestServerContext test = new();
        string csrf = await test.GetCsrfTokenAsync().ConfigureAwait(true);
        test.Client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);

        using HttpResponseMessage weak = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "short", "zh-Hans", null)).ConfigureAwait(true);
        Assert.False(weak.IsSuccessStatusCode);

        using HttpResponseMessage commonWeak = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "password1234", "zh-Hans", null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, commonWeak.StatusCode);
        using (var commonWeakBody = JsonDocument.Parse(await commonWeak.Content.ReadAsStringAsync().ConfigureAwait(true)))
        {
            Assert.Equal("weak-password", commonWeakBody.RootElement.GetProperty("code").GetString());
        }

        using HttpResponseMessage initialized = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, initialized.StatusCode);

        using HttpResponseMessage duplicate = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("other", "Other", "another-strong-password", "zh-Hans", null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using HttpResponseMessage denied = await test.Client.GetAsync(new Uri("/api/v1/users", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using HttpResponseMessage invalidLogin = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new SignInRequest("owner", "incorrect-password")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);

        using HttpResponseMessage login = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using HttpResponseMessage createdUser = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            new CreateUserRequest("operator", "Operator", "operator-strong-password", "User")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode);

        using HttpResponseMessage users = await test.Client.GetAsync(new Uri("/api/v1/users", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        string usersJson = await users.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Contains("OPERATOR", usersJson, StringComparison.Ordinal);

        using HttpResponseMessage createdKey = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/api-keys", UriKind.Relative),
            new CreateApiKeyRequest("integration", "users:read", null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, createdKey.StatusCode);
        CreatedApiKeyDto key = (await createdKey.Content.ReadFromJsonAsync<CreatedApiKeyDto>().ConfigureAwait(true))!;

        using HttpClient apiClient = test.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.PlaintextKey);
        using HttpResponseMessage apiResponse = await apiClient.GetAsync(new Uri("/api/v1/users", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);

        using HttpResponseMessage overviewResponse = await test.Client.GetAsync(new Uri("/api/v1/users/overview", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        UserOverviewDto overview = (await overviewResponse.Content.ReadFromJsonAsync<UserOverviewDto>().ConfigureAwait(true))!;
        Assert.Equal(2, overview.Total);
        Assert.Equal(1, overview.RoleCounts["Owner"]);
        Assert.Equal(1, overview.RoleCounts["User"]);
        Assert.Equal(1, overview.ActiveSessions);

        using HttpResponseMessage logout = await test.Client.PostAsync(new Uri("/api/v1/auth/logout", UriKind.Relative), null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using HttpResponseMessage afterLogout = await test.Client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-190")]
    [Trait("TestId", "SEC-040")]
    public async Task DesktopOidc换票_绑定Pkce且只能消费一次()
    {
        using TestServerContext test = new();
        string csrf = await test.GetCsrfTokenAsync().ConfigureAwait(true);
        test.Client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        using HttpResponseMessage initialized = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
        UserDto owner = (await initialized.Content.ReadFromJsonAsync<UserDto>().ConfigureAwait(true))!;
        IdentityApplicationService identity = test.Factory.Services.GetRequiredService<IdentityApplicationService>();
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        ApplicationResult<string> ticket = await identity.IssueDesktopOidcTicketAsync(owner.Id, challenge, CancellationToken.None).ConfigureAwait(true);
        Assert.True(ticket.Succeeded);

        using HttpResponseMessage wrong = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/local-session", UriKind.Relative),
            new LocalSessionExchangeRequest(ticket.Value!, Base64Url(RandomNumberGenerator.GetBytes(48)))).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using HttpResponseMessage exchanged = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/local-session", UriKind.Relative),
            new LocalSessionExchangeRequest(ticket.Value!, verifier)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);

        using HttpResponseMessage replay = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/local-session", UriKind.Relative),
            new LocalSessionExchangeRequest(ticket.Value!, verifier)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1080")]
    public async Task 用户批量禁用与删除_逐项报告且Owner保持不可变()
    {
        using TestServerContext test = new();
        string csrf = await test.GetCsrfTokenAsync().ConfigureAwait(true);
        test.Client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        using HttpResponseMessage initialized = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
        UserDto owner = (await initialized.Content.ReadFromJsonAsync<UserDto>().ConfigureAwait(true))!;
        using HttpResponseMessage login = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        UserDto disabledTarget = await CreateUserAsync(test.Client, "disabled-target").ConfigureAwait(true);
        UserDto deletedTarget = await CreateUserAsync(test.Client, "deleted-target").ConfigureAwait(true);

        using HttpResponseMessage disabledResponse = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/users/batch/disable", UriKind.Relative),
            new BatchUsersRequest([disabledTarget.Id, owner.Id])).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, disabledResponse.StatusCode);
        BatchUsersResultDto disabled = (await disabledResponse.Content.ReadFromJsonAsync<BatchUsersResultDto>().ConfigureAwait(true))!;
        Assert.True(disabled.Items.Single(item => item.UserId == disabledTarget.Id).Succeeded);
        Assert.Equal("owner-immutable", disabled.Items.Single(item => item.UserId == owner.Id).ErrorCode);

        using HttpClient anonymous = test.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        string anonymousCsrf = await GetCsrfTokenAsync(anonymous).ConfigureAwait(true);
        anonymous.DefaultRequestHeaders.Add("X-CSRF-Token", anonymousCsrf);
        using HttpResponseMessage disabledLogin = await anonymous.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new SignInRequest("disabled-target", "user-strong-password")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, disabledLogin.StatusCode);

        using HttpResponseMessage deletedResponse = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/users/batch/delete", UriKind.Relative),
            new BatchUsersRequest([deletedTarget.Id, owner.Id])).ConfigureAwait(true);
        BatchUsersResultDto deleted = (await deletedResponse.Content.ReadFromJsonAsync<BatchUsersResultDto>().ConfigureAwait(true))!;
        Assert.True(deleted.Items.Single(item => item.UserId == deletedTarget.Id).Succeeded);
        Assert.Equal("owner-immutable", deleted.Items.Single(item => item.UserId == owner.Id).ErrorCode);

        using HttpResponseMessage deletedLookup = await test.Client.GetAsync(new Uri($"/api/v1/users/{deletedTarget.Id:D}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, deletedLookup.StatusCode);

        using HttpResponseMessage duplicate = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/users/batch/disable", UriKind.Relative),
            new BatchUsersRequest([disabledTarget.Id, disabledTarget.Id])).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-230")]
    [Trait("TestId", "SEC-041")]
    public async Task 幂等键_重放首次响应并拒绝不同请求体与非法格式()
    {
        using TestServerContext test = new();
        string csrf = await test.GetCsrfTokenAsync().ConfigureAwait(true);
        test.Client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        using HttpResponseMessage initialized = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, initialized.StatusCode);
        using HttpResponseMessage login = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var key = Guid.Parse("20000000-0000-4000-8000-000000000001");
        using HttpRequestMessage firstRequest = CreateUserRequestMessage(key, "idempotent-user");
        using HttpResponseMessage first = await test.Client.SendAsync(firstRequest).ConfigureAwait(true);
        string firstBody = await first.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using HttpRequestMessage replayRequest = CreateUserRequestMessage(key, "idempotent-user");
        using HttpResponseMessage replay = await test.Client.SendAsync(replayRequest).ConfigureAwait(true);
        Assert.Equal(first.StatusCode, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());

        using HttpRequestMessage conflictRequest = CreateUserRequestMessage(key, "different-user");
        using HttpResponseMessage conflict = await test.Client.SendAsync(conflictRequest).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync().ConfigureAwait(true)))
        {
            Assert.Equal("idempotency-conflict", body.RootElement.GetProperty("code").GetString());
        }

        using HttpRequestMessage malformed = CreateUserRequestMessage(Guid.NewGuid(), "never-created");
        _ = malformed.Headers.Remove("Idempotency-Key");
        _ = malformed.Headers.TryAddWithoutValidation("Idempotency-Key", "invalid");
        using HttpResponseMessage rejected = await test.Client.SendAsync(malformed).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using HttpResponseMessage users = await test.Client.GetAsync(new Uri("/api/v1/users?name=idempotent-user", UriKind.Relative)).ConfigureAwait(true);
        CursorPage<UserDto> page = (await users.Content.ReadFromJsonAsync<CursorPage<UserDto>>().ConfigureAwait(true))!;
        _ = Assert.Single(page.Items);
        Assert.Equal("IDEMPOTENT-USER", page.Items[0].UserName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-240")]
    public async Task 用户游标分页_数据插入后不重复且继续返回新位置()
    {
        using TestServerContext test = new();
        string csrf = await test.GetCsrfTokenAsync().ConfigureAwait(true);
        test.Client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        using HttpResponseMessage initialized = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/initialize", UriKind.Relative),
            new InitializeRequest("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, initialized.StatusCode);
        using HttpResponseMessage login = await test.Client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new SignInRequest("owner", "correct-horse-battery-staple")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _ = await CreateUserAsync(test.Client, "alpha-user").ConfigureAwait(true);
        _ = await CreateUserAsync(test.Client, "charlie-user").ConfigureAwait(true);
        _ = await CreateUserAsync(test.Client, "echo-user").ConfigureAwait(true);

        CursorPage<UserDto> first = await GetUserPageAsync(test.Client, null, 2).ConfigureAwait(true);
        _ = await CreateUserAsync(test.Client, "delta-user").ConfigureAwait(true);
        CursorPage<UserDto> second = await GetUserPageAsync(test.Client, first.NextCursor, 2).ConfigureAwait(true);
        CursorPage<UserDto> third = await GetUserPageAsync(test.Client, second.NextCursor, 2).ConfigureAwait(true);
        string[] observed = [.. first.Items.Concat(second.Items).Concat(third.Items).Select(user => user.UserName)];

        Assert.Equal(["ALPHA-USER", "CHARLIE-USER", "DELTA-USER", "ECHO-USER", "OWNER"], observed);
        Assert.Equal(observed.Length, observed.Distinct(StringComparer.Ordinal).Count());
        Assert.Null(third.NextCursor);
    }

    private static async Task<CursorPage<UserDto>> GetUserPageAsync(HttpClient client, string? cursor, int limit)
    {
        string path = "/api/v1/users?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CursorPage<UserDto>>().ConfigureAwait(true))!;
    }

    private static HttpRequestMessage CreateUserRequestMessage(Guid idempotencyKey, string userName)
    {
        HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/users", UriKind.Relative))
        {
            Content = JsonContent.Create(new CreateUserRequest(userName, userName, "user-strong-password", "User")),
        };
        _ = request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private static async Task<UserDto> CreateUserAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            new CreateUserRequest(userName, userName, "user-strong-password", "User")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserDto>().ConfigureAwait(true))!;
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        using HttpResponseMessage state = await client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.True(state.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies));
        string cookie = cookies.Single(value => value.StartsWith("servitium.csrf=", StringComparison.Ordinal));
        return cookie.Split(';')[0].Split('=')[1];
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class TestServerContext : IDisposable
    {
        internal TestServerContext()
        {
            DataRoot = Path.Combine(Path.GetTempPath(), $"servitium-identity-test-{Guid.NewGuid():N}");
            BaseFactory = new WebApplicationFactory<Program>();
            Factory = BaseFactory.WithWebHostBuilder(builder =>
                builder.UseSetting("Servitium:DataRoot", DataRoot));
            Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = true,
            });
        }

        internal string DataRoot { get; }
        private WebApplicationFactory<Program> BaseFactory { get; }
        internal WebApplicationFactory<Program> Factory { get; }
        internal HttpClient Client { get; }

        internal async Task<string> GetCsrfTokenAsync()
        {
            using HttpResponseMessage state = await Client.GetAsync(new Uri("/api/v1/auth/state", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            IEnumerable<string> cookies = state.Headers.GetValues("Set-Cookie");
            string csrfCookie = cookies.Single(value => value.StartsWith("servitium.csrf=", StringComparison.Ordinal));
            return csrfCookie["servitium.csrf=".Length..csrfCookie.IndexOf(';', StringComparison.Ordinal)];
        }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            BaseFactory.Dispose();
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, true);
            }
        }
    }
}

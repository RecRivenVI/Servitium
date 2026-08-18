using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Common;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Operations;

using Xunit;

namespace Servitium.Client.ContractTests;

public sealed class ServitiumApiClientTests
{
    [Theory]
    [InlineData("relative")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("http://user:secret@127.0.0.1/")]
    [InlineData("http://127.0.0.1/?query=true")]
    [Trait("Category", "Contract")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "CTR-100")]
    public void 非法Server基址会被拒绝(string value)
    {
        Uri uri = new(value, UriKind.RelativeOrAbsolute);
        _ = Assert.Throws<ArgumentException>(() => new ServitiumApiClient(uri));
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "CTR-100")]
    public async Task 幂等读取只对瞬时错误执行有界重试()
    {
        int attempts = 0;
        using DelegateHandler handler = new((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(HttpStatusCode.OK, new AuthenticationBootstrapDto(true, false, null, null, false, null)));
        });
        using ServitiumApiClient client = new(
            new Uri("http://127.0.0.1:5344"),
            handler: handler);

        AuthenticationBootstrapDto result = await client.GetAuthenticationBootstrapAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.Initialized);
        Assert.Equal(3, attempts);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "CTR-100")]
    public async Task 变更请求不重试且ProblemDetails保留稳定字段()
    {
        int attempts = 0;
        using DelegateHandler handler = new((_, _) =>
        {
            attempts++;
            return Task.FromResult(RateLimited());
        });
        using ServitiumApiClient client = new(
            new Uri("http://127.0.0.1:5344"),
            handler: handler);

        ServitiumApiException? error = null;
        try
        {
            _ = await client.UpdateProfileAsync(new UpdateProfileRequest("Owner", "zh-Hans"), CancellationToken.None).ConfigureAwait(true);
        }
        catch (ServitiumApiException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
        Assert.Equal(1, attempts);
        Assert.Equal("rate-limited", error.Code);
        Assert.Equal("trace-42", error.TraceId);
        Assert.Equal("login", error.Parameters["scope"]);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("TestId", "CTR-121")]
    public async Task 客户端拒绝OpenApi未声明的成功状态码()
    {
        using DelegateHandler handler = new((_, _) => Task.FromResult(
            Json(HttpStatusCode.Accepted, new AuthenticationBootstrapDto(true, false, null, null, false, null))));
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:5344"), handler: handler);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetAuthenticationBootstrapAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("success status", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("TestId", "CTR-121")]
    public async Task 客户端拒绝OpenApi未声明的成功媒体类型()
    {
        using DelegateHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        }));
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:5344"), handler: handler);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetAuthenticationBootstrapAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("content type", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("TestId", "CTR-121")]
    public async Task 无请求体操作不会伪造空Json对象()
    {
        bool bodyWasAbsent = false;
        HmclIntegrityResultDto expected = new(DateTimeOffset.UnixEpoch, 1, 2, []);
        using DelegateHandler handler = new((request, _) =>
        {
            bodyWasAbsent = request.Content is null;
            return Task.FromResult(Json(HttpStatusCode.OK, expected));
        });
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:5344"), handler: handler);

        HmclIntegrityResultDto actual = await client.RunHmclIntegrityAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(bodyWasAbsent);
        Assert.Equal(expected.CheckedAt, actual.CheckedAt);
        Assert.Equal(expected.CheckedChannels, actual.CheckedChannels);
        Assert.Equal(expected.CheckedReleases, actual.CheckedReleases);
        Assert.Empty(actual.Failures);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("TestId", "CTR-121")]
    public async Task 服务端归档上传携带完整实例初始配置()
    {
        string? mediaType = null;
        string? body = null;
        using DelegateHandler handler = new(async (request, cancellationToken) =>
        {
            mediaType = request.Content?.Headers.ContentType?.MediaType;
            body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Json(HttpStatusCode.Accepted, new OperationAcceptedDto(Guid.NewGuid(), "ImportServerArchive", "Pending"));
        });
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:5344"), handler: handler);
        StructuredLaunchRequest launch = new(
            Guid.Parse("10000000-0000-4000-8000-000000000001"),
            1024,
            4096,
            ["-Dsynthetic=true"],
            "jar",
            "server.jar",
            ["nogui"],
            new Dictionary<string, string> { ["SYNTHETIC"] = "1" });
        using MemoryStream archive = new(Encoding.UTF8.GetBytes("synthetic-zip"));

        _ = await client.ImportServerArchiveAsync(
            archive,
            "synthetic.zip",
            "Synthetic Server",
            Guid.Parse("20000000-0000-4000-8000-000000000002"),
            launch,
            "save-all stop",
            120,
            ["test", "vanilla"],
            "Synthetic notes",
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("multipart/form-data", mediaType);
        Assert.Contains("name=stopCommand", body, StringComparison.Ordinal);
        Assert.Contains("save-all stop", body, StringComparison.Ordinal);
        Assert.Contains("name=stopTimeoutSeconds", body, StringComparison.Ordinal);
        Assert.Contains("120", body, StringComparison.Ordinal);
        Assert.Equal(2, Count(body!, "name=tags"));
        Assert.Contains("Synthetic notes", body, StringComparison.Ordinal);
        Assert.Contains("server.jar", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("TestId", "CTR-121")]
    public async Task 日志导出完整继承当前筛选条件且不携带分页游标()
    {
        List<Uri> requests = [];
        using DelegateHandler handler = new((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("header\r\n", Encoding.UTF8, "text/csv"),
            });
        });
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:5344"), handler: handler);
        DateTimeOffset from = new(2026, 8, 4, 1, 2, 3, TimeSpan.Zero);
        DateTimeOffset to = from.AddHours(2);
        using MemoryStream activity = new();
        using MemoryStream audit = new();

        _ = await client.ExportActivityLogsAsync(
            new ActivityQuery("alice", "instance.start", "instance:42", "success", from, to, "ignored-cursor", 7),
            activity,
            CancellationToken.None).ConfigureAwait(true);
        _ = await client.ExportSecurityAuditAsync(
            new SecurityAuditQuery("bob", "Authorization", "grant.revoke", "user:7", "denied", from, to, "ignored-cursor", 9),
            audit,
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, requests.Count);
        Assert.Equal("alice", Query(requests[0], "actor"));
        Assert.Equal("instance.start", Query(requests[0], "action"));
        Assert.Equal("instance:42", Query(requests[0], "resource"));
        Assert.Equal("success", Query(requests[0], "outcome"));
        Assert.Equal(from.ToString("O"), Query(requests[0], "from"));
        Assert.Equal(to.ToString("O"), Query(requests[0], "to"));
        Assert.DoesNotContain("cursor=", requests[0].Query, StringComparison.Ordinal);
        Assert.Equal("Authorization", Query(requests[1], "category"));
        Assert.Equal("grant.revoke", Query(requests[1], "action"));
        Assert.Equal("user:7", Query(requests[1], "resource"));
        Assert.Equal("denied", Query(requests[1], "outcome"));
        Assert.DoesNotContain("limit=", requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("TestId", "CTR-123")]
    public async Task 可选Guid筛选为空时不会发送空查询参数()
    {
        List<Uri> requests = [];
        using DelegateHandler handler = new((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/instances" => Json(HttpStatusCode.OK, new CursorPage<InstanceDto>([], null)),
                "/api/v1/operations" => Json(HttpStatusCode.OK, new CursorPage<OperationDetailsDto>([], null)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        });
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:5344"), handler: handler);

        _ = await client.ListInstancesAsync(null, null, null, [], null, 50, CancellationToken.None).ConfigureAwait(true);
        _ = await client.ListOperationsAsync(null, null, null, null, null, 50, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain("nodeId=", requests[0].Query, StringComparison.Ordinal);
        Assert.Contains("limit=50", requests[0].Query, StringComparison.Ordinal);
        Assert.DoesNotContain("initiatedBy=", requests[1].Query, StringComparison.Ordinal);
        Assert.Contains("limit=50", requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("TestId", "CTR-123")]
    public async Task 可选Guid筛选有值时保留规范查询参数()
    {
        List<Uri> requests = [];
        using DelegateHandler handler = new((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/instances" => Json(HttpStatusCode.OK, new CursorPage<InstanceDto>([], null)),
                "/api/v1/operations" => Json(HttpStatusCode.OK, new CursorPage<OperationDetailsDto>([], null)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        });
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:5344"), handler: handler);
        Guid nodeId = Guid.Parse("10000000-0000-4000-8000-000000000001");
        Guid userId = Guid.Parse("20000000-0000-4000-8000-000000000002");

        _ = await client.ListInstancesAsync(nodeId, null, null, [], null, 50, CancellationToken.None).ConfigureAwait(true);
        _ = await client.ListOperationsAsync(null, null, null, userId, null, 50, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(nodeId.ToString("D"), Query(requests[0], "nodeId"));
        Assert.Equal(userId.ToString("D"), Query(requests[1], "initiatedBy"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1500")]
    public async Task Secure会话Cookie在回环Http中由客户端显式携带且变更包含Csrf()
    {
        int stateReads = 0;
        bool loginProtected = false;
        bool profileProtected = false;
        bool postHasIdempotencyKey = false;
        UserDto user = new(Guid.Parse("00000000-0000-4000-8000-000000000001"), "owner", "Owner", "Owner", false, null, DateTimeOffset.UnixEpoch);
        PrincipalDto principal = new(user, [], "zh-Hans");
        using DelegateHandler handler = new(async (request, cancellationToken) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/auth/state")
            {
                stateReads++;
                HttpResponseMessage state = Json(HttpStatusCode.OK, new AuthenticationStateDto(true, false, null));
                _ = state.Headers.TryAddWithoutValidation("Set-Cookie", "servitium.csrf=csrf-token; Secure; HttpOnly; SameSite=Strict");
                return state;
            }

            if (path == "/api/v1/auth/login")
            {
                loginProtected = request.Headers.Contains("X-CSRF-Token") && Cookie(request).Contains("servitium.csrf=csrf-token", StringComparison.Ordinal);
                postHasIdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? keys) &&
                    Guid.TryParseExact(string.Join(string.Empty, keys), "D", out _);
                _ = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                HttpResponseMessage signedIn = Json(HttpStatusCode.OK, principal);
                _ = signedIn.Headers.TryAddWithoutValidation("Set-Cookie", "servitium.session=session-token; Secure; HttpOnly; SameSite=Strict");
                return signedIn;
            }

            profileProtected = request.Headers.Contains("X-CSRF-Token") &&
                Cookie(request).Contains("servitium.csrf=csrf-token", StringComparison.Ordinal) &&
                Cookie(request).Contains("servitium.session=session-token", StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, user with { DisplayName = "Changed" });
        });
        using ServitiumApiClient client = new(
            new Uri("http://127.0.0.1:5344"),
            handler: handler);

        _ = await client.Session.SignInAsync(new SignInRequest("owner", "password"), CancellationToken.None).ConfigureAwait(true);
        UserDto changed = await client.UpdateProfileAsync(new UpdateProfileRequest("Changed", "zh-Hans"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(1, stateReads);
        Assert.True(loginProtected);
        Assert.True(postHasIdempotencyKey);
        Assert.True(profileProtected);
        Assert.Equal("Changed", changed.DisplayName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-190")]
    public async Task DesktopOidc_随机回环回调使用Pkce换取客户端会话()
    {
        string? challenge = null;
        UserDto user = new(Guid.Parse("00000000-0000-4000-8000-000000000001"), "owner", "Owner", "Owner", false, null, DateTimeOffset.UnixEpoch);
        PrincipalDto principal = new(user, [], "zh-Hans");
        using DelegateHandler handler = new(async (request, cancellationToken) =>
        {
            LocalSessionExchangeRequest body = (await request.Content!.ReadFromJsonAsync<LocalSessionExchangeRequest>(cancellationToken).ConfigureAwait(false))!;
            Assert.Equal("desktop-code", body.Token);
            Assert.NotNull(body.CodeVerifier);
            Assert.Equal(challenge, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(body.CodeVerifier))));
            HttpResponseMessage response = Json(HttpStatusCode.OK, principal);
            _ = response.Headers.TryAddWithoutValidation("Set-Cookie", "servitium.session=oidc-session; Secure; HttpOnly; SameSite=Strict");
            return response;
        });
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:5344"), handler: handler);

        PrincipalDto result = await client.Session.SignInWithOidcAsync(
            authorization =>
            {
                Uri callback = new(Query(authorization, "desktopCallback"), UriKind.Absolute);
                challenge = Query(authorization, "desktopCodeChallenge");
                _ = Task.Run(async () =>
                {
                    using HttpClient browser = new();
                    using HttpResponseMessage response = await browser.GetAsync(
                        new Uri(callback.AbsoluteUri + "?code=desktop-code", UriKind.Absolute),
                        CancellationToken.None).ConfigureAwait(false);
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                });
                return Task.FromResult(true);
            },
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(user.Id, result.User.Id);
        Assert.Equal(user.Id, client.Session.Principal?.User.Id);
    }

    private static string Cookie(HttpRequestMessage request)
    {
        return request.Headers.TryGetValues("Cookie", out IEnumerable<string>? values) ? string.Join(';', values) : string.Empty;
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value)
    {
        return new HttpResponseMessage(status) { Content = JsonContent.Create(value) };
    }

    private static HttpResponseMessage RateLimited()
    {
        HttpResponseMessage response = Json(
            HttpStatusCode.TooManyRequests,
            new { code = "rate-limited", traceId = "trace-42", parameters = new Dictionary<string, string> { ["scope"] = "login" } });
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        return response;
    }

    private static string Query(Uri uri, string name)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && Uri.UnescapeDataString(pair[..separator]) == name)
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        throw new InvalidOperationException("The expected query value was not present.");
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int Count(string value, string token)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return send(request, cancellationToken);
        }
    }
}

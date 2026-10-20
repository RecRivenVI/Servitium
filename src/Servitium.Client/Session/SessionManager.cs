using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Identity;

namespace Servitium.Client.Session;

/// <summary>管理 CSRF、密码/TOTP、本机换票、会话恢复与续期。</summary>
public sealed class SessionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ServitiumApiClient _client;
    private readonly ISessionTokenStore? _tokenStore;

    internal SessionManager(ServitiumApiClient client, ISessionTokenStore? tokenStore)
    {
        _client = client;
        _tokenStore = tokenStore;
    }

    /// <summary>当前身份。</summary>
    public PrincipalDto? Principal { get; private set; }

    /// <summary>读取认证状态并建立 CSRF Cookie。</summary>
    public async Task<AuthenticationStateDto> GetStateAsync(CancellationToken cancellationToken)
    {
        AuthenticationStateDto state = await _client.GetJsonAsync<AuthenticationStateDto>("api/v1/auth/state", cancellationToken).ConfigureAwait(false);
        Principal = state.Principal;
        return state;
    }

    /// <summary>从平台保护存储恢复会话并向 Server 验证。</summary>
    public async Task<bool> RestoreAsync(CancellationToken cancellationToken)
    {
        if (_tokenStore is null)
        {
            return false;
        }

        string? token = await _tokenStore.ReadAsync(ServerKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        _client.RestoreSessionToken(token);
        try
        {
            Principal = await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ServitiumApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            _client.ClearSession();
            await _tokenStore.WriteAsync(ServerKey, null, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>创建首个 Owner。</summary>
    public async Task InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        _ = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        await _client.SendJsonNoContentAsync(HttpMethod.Post, "api/v1/auth/initialize", request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行密码登录；需要 TOTP 时保留受限会话供下一步使用。</summary>
    public async Task<SignInResult> SignInAsync(SignInRequest request, CancellationToken cancellationToken)
    {
        _ = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage message = _client.CreateRequest(HttpMethod.Post, "api/v1/auth/login", includeCsrf: true);
        message.Content = JsonContent.Create(request, options: JsonOptions);
        using HttpResponseMessage response = await _client.SendMutationAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return new(true, null);
        }

        PrincipalDto principal = await ServitiumApiClient.ReadJsonAsync<PrincipalDto>(response, cancellationToken).ConfigureAwait(false);
        Principal = principal;
        await PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        await _client.Events.ReconnectForSessionAsync(cancellationToken).ConfigureAwait(false);
        return new(false, principal);
    }

    /// <summary>完成 TOTP 或恢复码验证。</summary>
    public async Task<PrincipalDto> CompleteTotpAsync(string code, CancellationToken cancellationToken)
    {
        PrincipalDto principal = await _client.SendJsonAsync<CompleteTotpSignInRequest, PrincipalDto>(
            HttpMethod.Post,
            "api/v1/auth/login/totp",
            new CompleteTotpSignInRequest(code),
            cancellationToken).ConfigureAwait(false);
        Principal = principal;
        await PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        await _client.Events.ReconnectForSessionAsync(cancellationToken).ConfigureAwait(false);
        return principal;
    }

    /// <summary>仅对解析为本机的 Server 尝试命名管道换票。</summary>
    public async Task<LocalSessionResult> TryLocalSessionAsync(
        string pipeName = "Servitium.LocalAuth.v1",
        CancellationToken cancellationToken = default)
    {
        if (!await IsLoopbackServerAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(false, null, "local-auth-remote-server", null);
        }

        try
        {
            using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using StreamReader reader = new(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            string request = JsonSerializer.Serialize(new LocalPipeRequest("issue-session", ThisAssemblyVersion()), JsonOptions);
            await writer.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            using JsonDocument? response = line is null ? null : JsonDocument.Parse(line);
            JsonElement root = response?.RootElement ?? default;
            string? sessionToken = TryGetString(root, "sessionToken");
            DateTimeOffset? expiresAt = TryGetDateTimeOffset(root, "expiresAt");
            if (sessionToken is null)
            {
                return new(false, null, TryGetNestedString(root, "error", "code") ?? "unauthenticated", expiresAt);
            }

            PrincipalDto principal = await _client.SendJsonAsync<LocalSessionExchangeRequest, PrincipalDto>(
                HttpMethod.Post,
                "api/v1/auth/local-session",
                new LocalSessionExchangeRequest(sessionToken),
                cancellationToken).ConfigureAwait(false);
            Principal = principal;
            await PersistSessionAsync(cancellationToken).ConfigureAwait(false);
            await _client.Events.ReconnectForSessionAsync(cancellationToken).ConfigureAwait(false);
            return new(true, principal, null, expiresAt);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new(false, null, "local-auth-unavailable", null);
        }
    }

    /// <summary>通过系统浏览器、随机回环回调和 PKCE 一次性换票完成 Desktop OIDC 登录。</summary>
    public async Task<PrincipalDto> SignInWithOidcAsync(
        Func<Uri, Task<bool>> openBrowser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string callbackPath = "/servitium-oidc/" + Base64Url(RandomNumberGenerator.GetBytes(24)) + "/";
        Uri callback = new($"http://127.0.0.1:{port}{callbackPath}", UriKind.Absolute);
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Uri authorization = _client.GetOidcAuthorizationUri(null, false, callback, challenge);
        if (!await openBrowser(authorization).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The system browser could not be opened.");
        }

        string code = await ReceiveOidcCodeAsync(listener, callbackPath, cancellationToken).ConfigureAwait(false);
        Principal = await _client.SendJsonAsync<LocalSessionExchangeRequest, PrincipalDto>(
            HttpMethod.Post,
            "api/v1/auth/local-session",
            new LocalSessionExchangeRequest(code, verifier),
            cancellationToken).ConfigureAwait(false);
        await PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        await _client.Events.ReconnectForSessionAsync(cancellationToken).ConfigureAwait(false);
        return Principal;
    }

    /// <summary>通过系统浏览器和随机回环完成地址，把 OIDC 身份绑定到当前账号。</summary>
    public async Task BindOidcAsync(Func<Uri, Task<bool>> openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string callbackPath = "/servitium-oidc-bind/" + Base64Url(RandomNumberGenerator.GetBytes(24)) + "/";
        Uri callback = new($"http://127.0.0.1:{port}{callbackPath}", UriKind.Absolute);
        OidcBindingStartDto start = await _client.BeginOidcBindingAsync(callback, cancellationToken).ConfigureAwait(false);
        if (!await openBrowser(start.AuthorizationUri).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The system browser could not be opened.");
        }

        await ReceiveOidcCompletionAsync(listener, callbackPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本机会话距离到期不足两分钟时重新换票。</summary>
    public async Task<LocalSessionResult?> RenewLocalSessionIfDueAsync(CancellationToken cancellationToken)
    {
        return _client.SessionExpiresAt is DateTimeOffset expiresAt && expiresAt <= _client.TimeProvider.GetUtcNow().AddMinutes(2)
            ? await TryLocalSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>读取当前登录主体。</summary>
    public async Task<PrincipalDto> GetCurrentAsync(CancellationToken cancellationToken)
    {
        Principal = await _client.GetJsonAsync<PrincipalDto>("api/v1/auth/me", cancellationToken).ConfigureAwait(false);
        return Principal;
    }

    /// <summary>注销当前会话并清理保护存储。</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.SendNoContentAsync(HttpMethod.Post, "api/v1/auth/logout", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Principal = null;
            _client.ClearSession();
            if (_tokenStore is not null)
            {
                await _tokenStore.WriteAsync(ServerKey, null, cancellationToken).ConfigureAwait(false);
            }

            await _client.Events.StopAsync(cancellationToken).ConfigureAwait(false);
            await _client.Terminal.DetachAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private string ServerKey => _client.BaseAddress.GetLeftPart(UriPartial.Authority).ToUpperInvariant();

    private async Task PersistSessionAsync(CancellationToken cancellationToken)
    {
        if (_tokenStore is not null && _client.SessionToken is string token)
        {
            await _tokenStore.WriteAsync(ServerKey, token, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsLoopbackServerAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(_client.BaseAddress.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(_client.BaseAddress.Host, out IPAddress? address))
        {
            return IPAddress.IsLoopback(address);
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(_client.BaseAddress.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(IPAddress.IsLoopback);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string ThisAssemblyVersion()
    {
        return typeof(SessionManager).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static async Task<string> ReceiveOidcCodeAsync(
        TcpListener listener,
        string callbackPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        for (int attempt = 0; attempt < 16; attempt++)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            NetworkStream stream = connection.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                using StreamReader reader = new(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                for (int header = 0; header < 100; header++)
                {
                    string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }
                }

                string? target = ParseRequestTarget(requestLine);
                string? code = target is null ? null : ReadCode(target, callbackPath);
                byte[] response = Encoding.ASCII.GetBytes(code is null
                    ? "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 204 No Content\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                if (code is not null)
                {
                    return code;
                }
            }
        }

        throw new InvalidOperationException("The OIDC loopback callback did not provide a valid code.");
    }

    private static async Task ReceiveOidcCompletionAsync(
        TcpListener listener,
        string callbackPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        for (int attempt = 0; attempt < 16; attempt++)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            NetworkStream stream = connection.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                using StreamReader reader = new(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                for (int header = 0; header < 100; header++)
                {
                    string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }
                }

                string? target = ParseRequestTarget(requestLine);
                bool completed = target is not null && string.Equals(
                    target.Split('?', 2, StringSplitOptions.None)[0], callbackPath, StringComparison.Ordinal);
                byte[] response = Encoding.ASCII.GetBytes(completed
                    ? "HTTP/1.1 204 No Content\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                if (completed)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException("The OIDC loopback callback did not complete binding.");
    }

    private static string? ParseRequestTarget(string? requestLine)
    {
        if (requestLine is null || requestLine.Length > 8192)
        {
            return null;
        }

        string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[0] == "GET" && parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)
            ? parts[1]
            : null;
    }

    private static string? ReadCode(string target, string callbackPath)
    {
        int queryIndex = target.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex <= 0 || !string.Equals(target[..queryIndex], callbackPath, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (string pair in target[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && string.Equals(Uri.UnescapeDataString(pair[..equals]), "code", StringComparison.Ordinal))
            {
                string code = Uri.UnescapeDataString(pair[(equals + 1)..]);
                return code.Length is > 0 and <= 512 ? code : null;
            }
        }

        return null;
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? TryGetNestedString(JsonElement element, string parent, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out JsonElement nested)
            ? TryGetString(nested, name)
            : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            value.TryGetDateTimeOffset(out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private sealed record LocalPipeRequest(string Action, string ClientVersion);
}

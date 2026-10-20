using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client.Http;
using Servitium.Client.Generated;
using Servitium.Client.Realtime;
using Servitium.Client.Session;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Operations;

namespace Servitium.Client;

/// <summary>Servitium Desktop 使用的强类型 Server 客户端。</summary>
public sealed partial class ServitiumApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private bool _disposed;

    /// <summary>创建客户端；Server 地址必须是绝对 HTTP(S) 地址且不能携带凭据、查询或片段。</summary>
    public ServitiumApiClient(
        Uri baseAddress,
        ISessionTokenStore? sessionTokenStore = null,
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null)
    {
        ValidateBaseAddress(baseAddress);
        BaseAddress = new Uri(baseAddress.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = BaseAddress;
        _httpClient.Timeout = TimeSpan.FromSeconds(100);
        Session = new SessionManager(this, sessionTokenStore);
        Events = new EventsConnection(this);
        Terminal = new TerminalConnection(this);
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>当前 Server 基址。</summary>
    public Uri BaseAddress { get; }

    /// <summary>登录、换票、续期与登出。</summary>
    public SessionManager Session { get; }

    /// <summary>全局事件流。</summary>
    public EventsConnection Events { get; }

    /// <summary>实例终端流。</summary>
    public TerminalConnection Terminal { get; }

    /// <summary>为独立终端窗口创建互不干扰的终端连接。</summary>
    public TerminalConnection CreateTerminalConnection()
    {
        ThrowIfDisposed();
        return new TerminalConnection(this);
    }

    /// <summary>用于会话续期判断的可测试时钟。</summary>
    public TimeProvider TimeProvider { get; }

    internal string? SessionToken { get; private set; }

    internal string? CsrfToken { get; private set; }

    internal DateTimeOffset? SessionExpiresAt { get; private set; }

    internal async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendReadAsync(HttpMethod.Get, path, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<T>> GetItemsAsync<T>(string path, CancellationToken cancellationToken)
    {
        CursorPage<T> envelope = await GetJsonAsync<CursorPage<T>>(path, cancellationToken).ConfigureAwait(false);
        return envelope.Items;
    }

    internal async Task<CursorPage<T>> GetPageAsync<T>(string path, CancellationToken cancellationToken)
    {
        return await GetJsonAsync<CursorPage<T>>(path, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: IsMutation(method));
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using HttpResponseMessage response = await SendMutationAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TResponse> SendNoBodyJsonAsync<TResponse>(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: IsMutation(method));
        using HttpResponseMessage response = await SendMutationAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TResponse> SendContentAsync<TResponse>(
        HttpMethod method,
        string path,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using (content)
        using (HttpRequestMessage request = CreateRequest(method, path, includeCsrf: IsMutation(method)))
        {
            request.Content = content;
            using HttpResponseMessage response = await SendMutationAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task SendJsonNoContentAsync<TRequest>(HttpMethod method, string path, TRequest payload, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: IsMutation(method));
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using HttpResponseMessage response = await SendMutationAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SendNoContentAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: IsMutation(method));
        using HttpResponseMessage response = await SendMutationAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T> SendOperationJsonAsync<TRequest, T>(
        HttpMethod method,
        string path,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(payload, options: JsonOptions);
        return await SendOperationContentAsync<T>(method, path, content, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<OperationAcceptedDto> QueueJsonAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: true);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using HttpResponseMessage response = await SendMutationAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<OperationAcceptedDto>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<OperationAcceptedDto> QueueNoContentAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: true);
        using HttpResponseMessage response = await SendMutationAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<OperationAcceptedDto>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<OperationAcceptedDto> QueueContentAsync(
        HttpMethod method,
        string path,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using (content)
        using (HttpRequestMessage request = CreateRequest(method, path, includeCsrf: true))
        {
            request.Content = content;
            using HttpResponseMessage response = await SendMutationAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync<OperationAcceptedDto>(response, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<T> SendOperationContentAsync<T>(
        HttpMethod method,
        string path,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using (content)
        using (HttpRequestMessage request = CreateRequest(method, path, includeCsrf: true))
        {
            request.Content = content;
            using HttpResponseMessage response = await SendMutationAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            OperationAcceptedDto accepted = await ReadJsonAsync<OperationAcceptedDto>(response, cancellationToken).ConfigureAwait(false);
            return await WaitForOperationResultAsync<T>(accepted.OperationId, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<T> WaitForOperationResultAsync<T>(Guid operationId, CancellationToken cancellationToken)
    {
        while (true)
        {
            OperationDetailsDto details = await GetJsonAsync<OperationDetailsDto>(
                $"api/v1/operations/{operationId:D}", cancellationToken).ConfigureAwait(false);
            if (details.Operation.Status == "Succeeded")
            {
                return typeof(T) == typeof(NoContentResult)
                    ? (T)(object)new NoContentResult()
                    : string.IsNullOrWhiteSpace(details.Result)
                    ? throw new InvalidDataException("The completed operation has no result payload.")
                    : JsonSerializer.Deserialize<T>(details.Result, JsonOptions)
                    ?? throw new InvalidDataException("The completed operation result is invalid.");
            }

            if (details.Operation.Status is "Failed" or "Cancelled")
            {
                throw new ServitiumApiException(System.Net.HttpStatusCode.Conflict, details.Operation.ErrorCode ?? details.Operation.Status);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<HttpResponseMessage> SendReadAsync(
        HttpMethod method,
        string path,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        HttpResponseMessage response = await RetryPolicy.SendAsync(
            _httpClient,
            () => CreateRequest(method, path, includeCsrf: false),
            completionOption,
            cancellationToken).ConfigureAwait(false);
        CaptureCookies(response);
        return response;
    }

    internal async Task<HttpResponseMessage> SendMutationAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateRequestContract(request);
        HttpResponseMessage response = await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        CaptureCookies(response);
        return response;
    }

    internal async Task<HttpResponseMessage> SendReadRequestAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateRequestContract(request);
        HttpResponseMessage response = await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        CaptureCookies(response);
        return response;
    }

    internal async Task<DownloadedFileInfo> DownloadToAsync(
        HttpMethod method,
        string path,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: IsMutation(method));
        using HttpResponseMessage response = IsMutation(method)
            ? await SendMutationAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
            : await SendReadRequestAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        string name = response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "download.bin";
        return new DownloadedFileInfo(name, response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentLength);
    }

    internal HttpRequestMessage CreateRequest(HttpMethod method, string path, bool includeCsrf)
    {
        ThrowIfDisposed();
        string relativePath = path.TrimStart('/');
        if (!GeneratedProtocolContract.Contains(method.Method, "/" + relativePath))
        {
            throw new InvalidOperationException("The requested operation is not present in the generated Server protocol contract.");
        }

        HttpRequestMessage request = new(method, new Uri(relativePath, UriKind.Relative));
        if (method == HttpMethod.Post)
        {
            // 每次逻辑调用只生成一次；调用方随后发送的就是这一条请求，Server 可安全消除重复提交。
            _ = request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));
        }

        List<string> cookies = [];
        if (CsrfToken is not null)
        {
            cookies.Add("servitium.csrf=" + CsrfToken);
        }

        if (SessionToken is not null)
        {
            cookies.Add("servitium.session=" + SessionToken);
        }

        if (cookies.Count > 0)
        {
            _ = request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        }

        if (includeCsrf && CsrfToken is not null)
        {
            _ = request.Headers.TryAddWithoutValidation("X-CSRF-Token", CsrfToken);
        }

        return request;
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ProblemDetailsMapper.MapAsync(response, cancellationToken).ConfigureAwait(false);
        }

        HttpRequestMessage? request = response.RequestMessage;
        if (request?.RequestUri is not null)
        {
            string path = ProtocolPath(request.RequestUri);
            if (!GeneratedProtocolContract.AllowsSuccessStatusCode(request.Method.Method, path, (int)response.StatusCode))
            {
                throw new InvalidDataException("The Server returned a success status that is not declared by the generated protocol contract.");
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType) &&
                !GeneratedProtocolContract.ProducesResponseContentType(request.Method.Method, path, mediaType))
            {
                throw new InvalidDataException("The Server returned a content type that is not declared by the generated protocol contract.");
            }
        }
    }

    internal static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Server returned an empty JSON response.");
    }

    internal void RestoreSessionToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        SessionToken = token;
    }

    internal void ClearSession()
    {
        SessionToken = null;
        SessionExpiresAt = null;
    }

    private void CaptureCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return;
        }

        foreach (string value in values)
        {
            CaptureCookie(value, "servitium.csrf", token => CsrfToken = token, null);
            CaptureCookie(value, "servitium.session", token => SessionToken = token, expires => SessionExpiresAt = expires);
        }
    }

    private static void CaptureCookie(string header, string name, Action<string> setToken, Action<DateTimeOffset?>? setExpires)
    {
        if (!header.StartsWith(name + "=", StringComparison.Ordinal))
        {
            return;
        }

        int separator = header.IndexOf(';', StringComparison.Ordinal);
        string token = header[(name.Length + 1)..(separator < 0 ? header.Length : separator)];
        setToken(token);
        if (token.Length == 0)
        {
            setExpires?.Invoke(null);
            return;
        }

        DateTimeOffset? expires = null;
        foreach (string part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("expires=", StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.TryParse(part[8..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                expires = parsed.ToUniversalTime();
            }
        }

        setExpires?.Invoke(expires);
    }

    private static bool IsMutation(HttpMethod method)
    {
        return method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;
    }

    private static void ValidateRequestContract(HttpRequestMessage request)
    {
        if (request.RequestUri is null || request.Content is null)
        {
            return;
        }

        string? mediaType = request.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new InvalidOperationException("A request body must declare its media type.");
        }

        string path = ProtocolPath(request.RequestUri);
        if (!GeneratedProtocolContract.AcceptsRequestContentType(request.Method.Method, path, mediaType))
        {
            throw new InvalidOperationException("The request content type is not declared by the generated Server protocol contract.");
        }
    }

    private static string ProtocolPath(Uri requestUri)
    {
        return requestUri.IsAbsoluteUri
            ? requestUri.AbsolutePath + requestUri.Query
            : "/" + requestUri.OriginalString.TrimStart('/');
    }

    private static void ValidateBaseAddress(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri ||
            (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseAddress.UserInfo) ||
            !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new ArgumentException("The Server address must be an absolute HTTP(S) origin without credentials, query, or fragment.", nameof(baseAddress));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Events.Dispose();
        Terminal.Dispose();
        _httpClient.Dispose();
    }

    internal sealed record NoContentResult;
}

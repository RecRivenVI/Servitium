using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Distribution;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;

namespace Servitium.Client;

/// <summary>范围受限测试前端使用的强类型 HTTP 客户端。</summary>
public sealed class ServitiumTestClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private string? _csrfToken;
    private string? _sessionToken;

    /// <summary>创建连接指定测试 Server 的客户端。</summary>
    public ServitiumTestClient(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The Server address must be absolute.", nameof(baseAddress));
        }

        _httpClient = new HttpClient { BaseAddress = baseAddress };
    }

    /// <summary>读取认证状态并取得 CSRF 令牌。</summary>
    public async Task<AuthenticationStateDto> GetAuthenticationStateAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/api/v1/auth/state", includeCsrf: false);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        CaptureCookie(response, "servitium.csrf", value => _csrfToken = value);
        return await ReadAsync<AuthenticationStateDto>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>初始化首个 Owner。</summary>
    public async Task InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = CreateRequest(HttpMethod.Post, "/api/v1/auth/initialize", includeCsrf: true);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>登录并保存短期会话。</summary>
    public async Task<PrincipalDto> SignInAsync(SignInRequest request, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = CreateRequest(HttpMethod.Post, "/api/v1/auth/login", includeCsrf: true);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        CaptureCookie(response, "servitium.session", value => _sessionToken = value);
        return await ReadAsync<PrincipalDto>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>列出在线或已登记节点。</summary>
    public Task<IReadOnlyList<NodeDto>> ListNodesAsync(CancellationToken cancellationToken)
    {
        return ReadItemsAsync<NodeDto>("/api/v1/nodes", cancellationToken);
    }

    /// <summary>创建一次性 Agent 注册令牌。</summary>
    public Task<NodeRegistrationTokenResponse> CreateNodeRegistrationTokenAsync(CreateNodeRegistrationTokenRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateNodeRegistrationTokenRequest, NodeRegistrationTokenResponse>(HttpMethod.Post, "/api/v1/nodes/registration-tokens", request, cancellationToken);
    }

    /// <summary>附加外部绝对路径。</summary>
    public Task<InstanceDto> AttachInstanceAsync(AttachInstanceDirectoryRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<AttachInstanceDirectoryRequest, InstanceDto>(HttpMethod.Post, "/api/v1/instances/attach", request, cancellationToken);
    }

    /// <summary>上传服务端 ZIP 并在 Agent 创建托管实例。</summary>
    public async Task<InstanceDto> ImportServerArchiveAsync(
        Guid nodeId,
        string name,
        string archivePath,
        StructuredLaunchRequest launch,
        CancellationToken cancellationToken)
    {
        using MultipartFormDataContent content = new();
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamContent file = new(stream);
        using StringContent node = new(nodeId.ToString("D"));
        using StringContent instanceName = new(name);
        using StringContent launchJson = new(JsonSerializer.Serialize(launch, JsonOptions), Encoding.UTF8, "application/json");
        content.Add(file, "file", Path.GetFileName(archivePath));
        content.Add(node, "nodeId");
        content.Add(instanceName, "name");
        content.Add(launchJson, "launch");
        return await SendOperationAsync<InstanceDto>(HttpMethod.Post, "/api/v1/instances/from-archive", content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>列出 HMCL Channel。</summary>
    public Task<IReadOnlyList<HmclChannelDto>> ListChannelsAsync(CancellationToken cancellationToken)
    {
        return ReadItemsAsync<HmclChannelDto>("/api/v1/hmcl/channels", cancellationToken);
    }

    /// <summary>创建公开 HMCL Channel。</summary>
    public Task<HmclChannelDto> CreateChannelAsync(CreateHmclChannelRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateHmclChannelRequest, HmclChannelDto>(HttpMethod.Post, "/api/v1/hmcl/channels", request, cancellationToken);
    }

    /// <summary>上传 Prism ZIP 并获得只读转换计划。</summary>
    public async Task<StagedPrismPreviewDto> PreviewPrismAsync(string archivePath, CancellationToken cancellationToken)
    {
        using MultipartFormDataContent content = new();
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamContent file = new(stream);
        content.Add(file, "file", Path.GetFileName(archivePath));
        return await SendAsync<StagedPrismPreviewDto>(HttpMethod.Post, "/api/v1/imports/prism/preview", content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把已预览 Prism ZIP 转换为不可变 Release。</summary>
    public async Task<HmclReleaseCreationResult> ConvertPrismAsync(Guid uploadId, ConvertStagedPrismRequest request, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(request);
        return await SendOperationAsync<HmclReleaseCreationResult>(
            HttpMethod.Post,
            $"/api/v1/imports/prism/{uploadId:D}/convert",
            content,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>上传普通内容 ZIP 并物化 Release。</summary>
    public async Task<HmclReleaseCreationResult> CreateReleaseFromArchiveAsync(
        Guid channelId,
        string archivePath,
        CreateHmclReleaseMetadataRequest metadata,
        CancellationToken cancellationToken)
    {
        using MultipartFormDataContent content = new();
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamContent file = new(stream);
        using StringContent metadataJson = new(JsonSerializer.Serialize(metadata, JsonOptions), Encoding.UTF8, "application/json");
        content.Add(file, "file", Path.GetFileName(archivePath));
        content.Add(metadataJson, "metadata");
        return await SendOperationAsync<HmclReleaseCreationResult>(
            HttpMethod.Post,
            $"/api/v1/hmcl/channels/{channelId:D}/releases",
            content,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取发布差异。</summary>
    public Task<HmclPublishDiffDto> PreviewPublishAsync(Guid channelId, Guid releaseId, CancellationToken cancellationToken)
    {
        return SendJsonAsync<PublishHmclChannelRequest, HmclPublishDiffDto>(
            HttpMethod.Post,
            $"/api/v1/hmcl/channels/{channelId:D}/preview",
            new PublishHmclChannelRequest(releaseId),
            cancellationToken);
    }

    /// <summary>通过门禁后原子发布 Release。</summary>
    public async Task<HmclPublishDiffDto> PublishAsync(Guid channelId, Guid releaseId, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(new PublishHmclChannelRequest(releaseId));
        return await SendOperationAsync<HmclPublishDiffDto>(
            HttpMethod.Post,
            $"/api/v1/hmcl/channels/{channelId:D}/publish",
            content,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest content, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: method != HttpMethod.Get);
        request.Content = JsonContent.Create(content);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using (content)
        {
            using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: method != HttpMethod.Get);
            request.Content = content;
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T> SendOperationAsync<T>(HttpMethod method, string path, HttpContent content, CancellationToken cancellationToken)
    {
        using (content)
        {
            using HttpRequestMessage request = CreateRequest(method, path, includeCsrf: true);
            request.Content = content;
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            OperationAcceptedDto accepted = await ReadAsync<OperationAcceptedDto>(response, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                using HttpRequestMessage statusRequest = CreateRequest(
                    HttpMethod.Get,
                    $"/api/v1/operations/{accepted.OperationId:D}",
                    includeCsrf: false);
                using HttpResponseMessage statusResponse = await _httpClient.SendAsync(statusRequest, cancellationToken).ConfigureAwait(false);
                OperationDetailsDto details = await ReadAsync<OperationDetailsDto>(statusResponse, cancellationToken).ConfigureAwait(false);
                if (details.Operation.Status == "Succeeded")
                {
                    return !string.IsNullOrWhiteSpace(details.Result)
                        ? JsonSerializer.Deserialize<T>(details.Result, JsonOptions) ?? throw new InvalidDataException("The Server operation returned an empty result.")
                        : throw new InvalidDataException("The Server operation completed without a result.");
                }

                if (details.Operation.Status is "Failed" or "Cancelled")
                {
                    throw new InvalidOperationException(details.Operation.ErrorCode ?? details.Operation.Status);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<T>> ReadItemsAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path, includeCsrf: false);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        ItemsEnvelope<T> envelope = await ReadAsync<ItemsEnvelope<T>>(response, cancellationToken).ConfigureAwait(false);
        return envelope.Items;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, bool includeCsrf)
    {
        HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));
        string cookies = string.Join("; ", new[]
        {
            _csrfToken is null ? null : "servitium.csrf=" + _csrfToken,
            _sessionToken is null ? null : "servitium.session=" + _sessionToken,
        }.Where(value => value is not null));
        if (cookies.Length > 0)
        {
            _ = request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        if (includeCsrf && _csrfToken is not null)
        {
            _ = request.Headers.TryAddWithoutValidation("X-CSRF-Token", _csrfToken);
        }

        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Server returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string code = "http-error";
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (body.RootElement.TryGetProperty("code", out JsonElement codeElement))
            {
                code = codeElement.GetString() ?? code;
            }
        }
        catch (JsonException)
        {
        }

        throw new ServitiumApiException(response.StatusCode, code);
    }

    private static void CaptureCookie(HttpResponseMessage response, string name, Action<string> assign)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return;
        }

        string? cookie = values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal));
        if (cookie is null)
        {
            return;
        }

        int start = name.Length + 1;
        assign(cookie[start..cookie.IndexOf(';', start)]);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "System.Text.Json instantiates this private response envelope through reflection.")]
    private sealed record ItemsEnvelope<T>(IReadOnlyList<T> Items);
}

/// <summary>Server 返回稳定错误码时抛出的客户端异常。</summary>
public sealed class ServitiumApiException : Exception
{
    /// <summary>创建空客户端异常。</summary>
    public ServitiumApiException()
    {
    }

    /// <summary>使用消息创建客户端异常。</summary>
    public ServitiumApiException(string message)
        : base(message)
    {
        Code = message;
    }

    /// <summary>使用消息和内部异常创建客户端异常。</summary>
    public ServitiumApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = message;
    }

    /// <summary>使用 HTTP 状态和稳定错误码创建客户端异常。</summary>
    public ServitiumApiException(HttpStatusCode statusCode, string code)
        : base(code)
    {
        StatusCode = statusCode;
        Code = code;
    }

    /// <summary>使用完整 Problem Details 创建客户端异常。</summary>
    public ServitiumApiException(
        HttpStatusCode statusCode,
        string code,
        string? traceId,
        IReadOnlyDictionary<string, string> parameters,
        TimeSpan? retryAfter)
        : base(code)
    {
        StatusCode = statusCode;
        Code = code;
        TraceId = traceId;
        Parameters = parameters;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>稳定错误码。</summary>
    public string Code { get; } = "http-error";

    /// <summary>服务端追踪号。</summary>
    public string? TraceId { get; }

    /// <summary>安全的结构化错误参数。</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>限流响应建议的等待时间。</summary>
    public TimeSpan? RetryAfter { get; }
}

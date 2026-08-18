using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Common;
using Servitium.Contracts.Java;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;

namespace Servitium.Client;

public sealed partial class ServitiumApiClient
{
    /// <summary>列出节点。</summary>
    public Task<CursorPage<NodeDto>> ListNodesAsync(CancellationToken cancellationToken)
    {
        return ListNodesAsync(null, null, null, 50, cancellationToken);
    }

    /// <summary>按状态和名称筛选节点，并使用不透明游标读取一页。</summary>
    public Task<CursorPage<NodeDto>> ListNodesAsync(
        string? status,
        string? name,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        return GetPageAsync<NodeDto>(
            $"api/v1/nodes?status={Escape(status)}&name={Escape(name)}&cursor={Escape(cursor)}&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>读取节点详情。</summary>
    public Task<NodeDetailsDto> GetNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<NodeDetailsDto>($"api/v1/nodes/{nodeId:D}", cancellationToken);
    }

    /// <summary>更新节点及 Agent 配置。</summary>
    public Task<NodeDto> UpdateNodeAsync(Guid nodeId, UpdateNodeRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpdateNodeRequest, NodeDto>(HttpMethod.Patch, $"api/v1/nodes/{nodeId:D}", request, cancellationToken);
    }

    /// <summary>移除节点；默认保留实例登记以防误操作。</summary>
    public Task RemoveNodeAsync(Guid nodeId, bool removeRegistrations, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/nodes/{nodeId:D}?removeRegistrations={Bool(removeRegistrations)}", cancellationToken);
    }

    /// <summary>创建一次性节点注册令牌。</summary>
    public Task<NodeRegistrationTokenResponse> CreateNodeRegistrationTokenAsync(
        CreateNodeRegistrationTokenRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateNodeRegistrationTokenRequest, NodeRegistrationTokenResponse>(
            HttpMethod.Post, "api/v1/nodes/registration-tokens", request, cancellationToken);
    }

    /// <summary>列出未过期的节点注册令牌摘要。</summary>
    public Task<CursorPage<NodeRegistrationTokenDto>> ListNodeRegistrationTokensAsync(CancellationToken cancellationToken)
    {
        return ListNodeRegistrationTokensAsync(null, 50, cancellationToken);
    }

    /// <summary>分页列出未过期的节点注册令牌摘要。</summary>
    public Task<CursorPage<NodeRegistrationTokenDto>> ListNodeRegistrationTokensAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<NodeRegistrationTokenDto>(
            $"api/v1/nodes/registration-tokens?cursor={Escape(cursor)}&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>撤销节点注册令牌。</summary>
    public Task RevokeNodeRegistrationTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/nodes/registration-tokens/{tokenId:D}", cancellationToken);
    }

    /// <summary>请求节点断开并重新建立控制连接。</summary>
    public Task ReconnectNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Post, $"api/v1/nodes/{nodeId:D}/reconnect", cancellationToken);
    }

    /// <summary>轮换节点凭据。</summary>
    public Task<NodeCredentialRotationDto> RotateNodeCredentialAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<NodeCredentialRotationDto>(
            HttpMethod.Post, $"api/v1/nodes/{nodeId:D}/credential/rotate", cancellationToken);
    }

    /// <summary>撤销节点凭据并断开连接。</summary>
    public Task RevokeNodeCredentialAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Post, $"api/v1/nodes/{nodeId:D}/credential/revoke", cancellationToken);
    }

    /// <summary>列出最近 24 小时内的节点指标。</summary>
    public Task<CursorPage<NodeMetricsDto>> ListNodeMetricsAsync(
        Guid nodeId,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        return ListNodeMetricsAsync(nodeId, since, null, 200, cancellationToken);
    }

    /// <summary>分页列出最近 24 小时内的节点指标。</summary>
    public Task<CursorPage<NodeMetricsDto>> ListNodeMetricsAsync(
        Guid nodeId,
        DateTimeOffset? since,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        return GetPageAsync<NodeMetricsDto>(
            $"api/v1/nodes/{nodeId:D}/metrics?since={Date(since)}&cursor={Escape(cursor)}&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>列出节点 Java 运行时。</summary>
    public Task<CursorPage<JavaRuntimeDto>> ListJavaRuntimesAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return ListJavaRuntimesAsync(nodeId, null, 50, cancellationToken);
    }

    /// <summary>分页列出节点 Java 运行时。</summary>
    public Task<CursorPage<JavaRuntimeDto>> ListJavaRuntimesAsync(Guid nodeId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<JavaRuntimeDto>(
            $"api/v1/nodes/{nodeId:D}/java-runtimes?cursor={Escape(cursor)}&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>让 Agent 探测 Java；返回可跟踪的长操作。</summary>
    public Task<OperationAcceptedDto> DiscoverJavaRuntimesAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return QueueNoContentAsync(HttpMethod.Post, $"api/v1/nodes/{nodeId:D}/java-runtimes/discover", cancellationToken);
    }

    /// <summary>登记 Agent 已存在的 java.exe。</summary>
    public Task<JavaRuntimeDto> RegisterJavaRuntimeAsync(
        Guid nodeId,
        RegisterJavaRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<RegisterJavaRuntimeRequest, JavaRuntimeDto>(
            HttpMethod.Post, $"api/v1/nodes/{nodeId:D}/java-runtimes", request, cancellationToken);
    }

    /// <summary>上传并导入 Java 归档；归档流不会在内存中缓冲。</summary>
    public async Task<OperationAcceptedDto> ImportJavaRuntimeAsync(
        Guid nodeId,
        Stream archive,
        string fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        using MultipartFormDataContent form = new();
        using StreamContent file = new(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(fileName));
        return await QueueContentAsync(
            HttpMethod.Post, $"api/v1/nodes/{nodeId:D}/java-runtimes/import", form, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取 Java 运行时详情。</summary>
    public Task<JavaRuntimeDetailsDto> GetJavaRuntimeAsync(Guid nodeId, Guid runtimeId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<JavaRuntimeDetailsDto>($"api/v1/nodes/{nodeId:D}/java-runtimes/{runtimeId:D}", cancellationToken);
    }

    /// <summary>取消登记 Java；删除文件和强制解除依赖必须显式指定。</summary>
    public Task<OperationAcceptedDto> UnregisterJavaRuntimeAsync(
        Guid nodeId,
        Guid runtimeId,
        bool deleteFiles,
        bool force,
        CancellationToken cancellationToken)
    {
        return QueueNoContentAsync(
            HttpMethod.Delete,
            $"api/v1/nodes/{nodeId:D}/java-runtimes/{runtimeId:D}?deleteFiles={Bool(deleteFiles)}&force={Bool(force)}",
            cancellationToken);
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string Date(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client.Http;
using Servitium.Contracts.Common;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Operations;

namespace Servitium.Client;

public sealed partial class ServitiumApiClient
{
    /// <summary>按节点与显示属性筛选实例。</summary>
    public Task<CursorPage<InstanceDto>> ListInstancesAsync(
        Guid? nodeId,
        string? status,
        string? name,
        string? tag,
        CancellationToken cancellationToken)
    {
        return ListInstancesAsync(nodeId, status, name, string.IsNullOrWhiteSpace(tag) ? [] : [tag], null, 50, cancellationToken);
    }

    /// <summary>按节点、状态、名称与多个标签筛选实例，并使用不透明游标读取一页。</summary>
    public Task<CursorPage<InstanceDto>> ListInstancesAsync(
        Guid? nodeId,
        string? status,
        string? name,
        IReadOnlyList<string> tags,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        string tagQuery = string.Join(string.Empty, tags.Select(tag => "&tag=" + Escape(tag)));
        return GetPageAsync<InstanceDto>(
            $"api/v1/instances?{OptionalGuidQuery("nodeId", nodeId)}status={Escape(status)}&name={Escape(name)}&cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}{tagQuery}",
            cancellationToken);
    }

    /// <summary>读取按节点分组的实例。</summary>
    public Task<InstanceGroupsDto> ListGroupedInstancesAsync(CancellationToken cancellationToken)
    {
        return GetJsonAsync<InstanceGroupsDto>("api/v1/instances/grouped", cancellationToken);
    }

    /// <summary>读取指定节点分组的下一页。</summary>
    public Task<InstanceGroupsDto> ListGroupedInstancesAsync(
        Guid nodeId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        return GetJsonAsync<InstanceGroupsDto>(
            $"api/v1/instances/grouped?nodeId={nodeId:D}&cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>读取实例详情。</summary>
    public Task<InstanceDetailsDto> GetInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<InstanceDetailsDto>($"api/v1/instances/{instanceId:D}", cancellationToken);
    }

    /// <summary>更新实例配置。</summary>
    public Task<InstanceDetailsDto> UpdateInstanceAsync(
        Guid instanceId,
        UpdateInstanceRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpdateInstanceRequest, InstanceDetailsDto>(
            HttpMethod.Patch, $"api/v1/instances/{instanceId:D}", request, cancellationToken);
    }

    /// <summary>创建由 Servitium 管理工作目录的空实例。</summary>
    public Task<OperationAcceptedDto> CreateInstanceAsync(CreateInstanceRequest request, CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, "api/v1/instances", request, cancellationToken);
    }

    /// <summary>登记节点上的任意绝对路径，不取得目录所有权。</summary>
    public Task<OperationAcceptedDto> AttachInstanceDirectoryAsync(
        AttachInstanceDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, "api/v1/instances/attach", request, cancellationToken);
    }

    /// <summary>流式上传服务端 ZIP 并开始导入。</summary>
    public async Task<OperationAcceptedDto> ImportServerArchiveAsync(
        Stream archive,
        string fileName,
        string name,
        Guid nodeId,
        StructuredLaunchRequest launch,
        CancellationToken cancellationToken)
    {
        return await ImportServerArchiveAsync(
            archive, fileName, name, nodeId, launch, "stop", 60, [], null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>流式上传服务端 ZIP，并携带完整的实例初始配置。</summary>
    public async Task<OperationAcceptedDto> ImportServerArchiveAsync(
        Stream archive,
        string fileName,
        string name,
        Guid nodeId,
        StructuredLaunchRequest launch,
        string stopCommand,
        int stopTimeoutSeconds,
        IReadOnlyList<string> tags,
        string? notes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using MultipartFormDataContent form = new();
        using StreamContent content = new(archive);
        using StringContent nodeContent = new(nodeId.ToString("D", CultureInfo.InvariantCulture), Encoding.UTF8);
        using StringContent nameContent = new(name, Encoding.UTF8);
        using StringContent launchContent = new(JsonSerializer.Serialize(launch, JsonOptions), Encoding.UTF8);
        using StringContent stopCommandContent = new(stopCommand, Encoding.UTF8);
        using StringContent stopTimeoutContent = new(stopTimeoutSeconds.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
        using StringContent notesContent = new(notes ?? string.Empty, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(content, "file", Path.GetFileName(fileName));
        form.Add(nodeContent, "nodeId");
        form.Add(nameContent, "name");
        form.Add(launchContent, "launch");
        form.Add(stopCommandContent, "stopCommand");
        form.Add(stopTimeoutContent, "stopTimeoutSeconds");
        List<StringContent> tagContents = [];
        foreach (string tag in tags)
        {
            StringContent tagContent = new(tag, Encoding.UTF8);
            tagContents.Add(tagContent);
            form.Add(tagContent, "tags");
        }

        form.Add(notesContent, "notes");
        try
        {
            return await QueueContentAsync(HttpMethod.Post, "api/v1/instances/from-archive", form, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (StringContent tagContent in tagContents)
            {
                tagContent.Dispose();
            }
        }
    }

    /// <summary>仅删除实例登记，永不递归删除外部目录。</summary>
    public Task UnregisterInstanceAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/instances/{instanceId:D}?nodeId={nodeId:D}", cancellationToken);
    }

    /// <summary>删除 Servitium 拥有的工作目录；外部目录会被 Server/Agent 拒绝。</summary>
    public Task<OperationAcceptedDto> DeleteInstanceWorkingDirectoryAsync(
        Guid instanceId,
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        return QueueNoContentAsync(
            HttpMethod.Delete, $"api/v1/instances/{instanceId:D}/working-directory?nodeId={nodeId:D}", cancellationToken);
    }

    /// <summary>启动实例。</summary>
    public Task<OperationAcceptedDto> StartInstanceAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return QueueNoContentAsync(HttpMethod.Post, InstanceActionPath(instanceId, nodeId, "start"), cancellationToken);
    }

    /// <summary>优雅停止实例。</summary>
    public Task<OperationAcceptedDto> StopInstanceAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return QueueNoContentAsync(HttpMethod.Post, InstanceActionPath(instanceId, nodeId, "stop"), cancellationToken);
    }

    /// <summary>重启实例。</summary>
    public Task<OperationAcceptedDto> RestartInstanceAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return QueueNoContentAsync(HttpMethod.Post, InstanceActionPath(instanceId, nodeId, "restart"), cancellationToken);
    }

    /// <summary>强制停止实例。</summary>
    public Task<OperationAcceptedDto> ForceStopInstanceAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return QueueNoContentAsync(HttpMethod.Post, InstanceActionPath(instanceId, nodeId, "force-stop"), cancellationToken);
    }

    /// <summary>发送控制台命令。</summary>
    public Task<InstanceRuntimeDto> SendInstanceCommandAsync(
        Guid instanceId,
        Guid nodeId,
        SendInstanceCommandRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<SendInstanceCommandRequest, InstanceRuntimeDto>(
            HttpMethod.Post, InstanceActionPath(instanceId, nodeId, "command"), request, cancellationToken);
    }

    /// <summary>接受 Mojang EULA。</summary>
    public Task AcceptEulaAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Post, InstanceActionPath(instanceId, nodeId, "eula/accept"), cancellationToken);
    }

    /// <summary>读取实例运行状态。</summary>
    public Task<InstanceRuntimeDto> GetInstanceStatusAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<InstanceRuntimeDto>(InstanceActionPath(instanceId, nodeId, "status"), cancellationToken);
    }

    /// <summary>读取实例资源指标。</summary>
    public Task<InstanceMetricsDto> GetInstanceMetricsAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<InstanceMetricsDto>(InstanceActionPath(instanceId, nodeId, "metrics"), cancellationToken);
    }

    /// <summary>读取 Query 玩家列表。</summary>
    public Task<PlayerListDto> GetInstancePlayersAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<PlayerListDto>(InstanceActionPath(instanceId, nodeId, "players"), cancellationToken);
    }

    /// <summary>经 RCON 发送命令。</summary>
    public Task<RconCommandResultDto> SendRconCommandAsync(
        Guid instanceId,
        Guid nodeId,
        RconCommandRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<RconCommandRequest, RconCommandResultDto>(
            HttpMethod.Post, InstanceActionPath(instanceId, nodeId, "rcon/command"), request, cancellationToken);
    }

    /// <summary>按行读取实例日志。</summary>
    public Task<InstanceLogDto> ReadInstanceLogAsync(
        Guid instanceId,
        Guid nodeId,
        string? file,
        long startLine,
        int count,
        string? search,
        CancellationToken cancellationToken)
    {
        return GetJsonAsync<InstanceLogDto>(
            InstanceActionPath(instanceId, nodeId, "logs") +
            $"&file={Escape(file)}&startLine={startLine.ToString(CultureInfo.InvariantCulture)}&count={count.ToString(CultureInfo.InvariantCulture)}&search={Escape(search)}",
            cancellationToken);
    }

    /// <summary>导出当前终端会话日志。</summary>
    public async Task<DownloadedFile> ExportTerminalSessionLogAsync(
        Guid instanceId,
        Guid nodeId,
        bool includeAnsi,
        CancellationToken cancellationToken)
    {
        string path = InstanceActionPath(instanceId, nodeId, "terminal/session-log") + "&includeAnsi=" + Bool(includeAnsi);
        using HttpResponseMessage response = await SendReadAsync(
            HttpMethod.Get, path, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string name = response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "session.log";
        return new DownloadedFile(name, response.Content.Headers.ContentType?.MediaType, content);
    }

    /// <summary>运行实例更新动作。</summary>
    public Task<OperationAcceptedDto> RunInstanceUpdateAsync(
        Guid instanceId,
        Guid nodeId,
        RunInstanceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, InstanceActionPath(instanceId, nodeId, "update"), request, cancellationToken);
    }

    /// <summary>执行批量实例动作。</summary>
    public Task<OperationAcceptedDto> RunBatchInstanceActionAsync(
        string action,
        BatchInstanceActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        string normalized = action.ToUpperInvariant();
        if (normalized is not ("START" or "STOP" or "RESTART" or "FORCE-STOP" or "UNREGISTER"))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        string routeAction = normalized switch
        {
            "START" => "start",
            "STOP" => "stop",
            "RESTART" => "restart",
            "FORCE-STOP" => "force-stop",
            _ => "unregister",
        };
        return QueueJsonAsync(HttpMethod.Post, "api/v1/instances/batch/" + routeAction, request, cancellationToken);
    }

    private static string InstanceActionPath(Guid instanceId, Guid nodeId, string action)
    {
        return $"api/v1/instances/{instanceId:D}/{action}?nodeId={nodeId:D}";
    }

    private static string OptionalGuidQuery(string name, Guid? value)
    {
        return value is Guid id ? $"{name}={id:D}&" : string.Empty;
    }
}

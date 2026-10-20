using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client.Http;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Operations;

namespace Servitium.Client;

public sealed partial class ServitiumApiClient
{
    /// <summary>列出 HMCL Channel。</summary>
    public Task<CursorPage<HmclChannelDto>> ListHmclChannelsAsync(CancellationToken cancellationToken)
    {
        return ListHmclChannelsAsync(null, 50, cancellationToken);
    }

    /// <summary>分页列出 HMCL Channel。</summary>
    public Task<CursorPage<HmclChannelDto>> ListHmclChannelsAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<HmclChannelDto>(
            $"api/v1/hmcl/channels?cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>创建 HMCL Channel；私有令牌只在本响应返回。</summary>
    public Task<HmclChannelDto> CreateHmclChannelAsync(
        CreateHmclChannelRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateHmclChannelRequest, HmclChannelDto>(
            HttpMethod.Post, "api/v1/hmcl/channels", request, cancellationToken);
    }

    /// <summary>读取 HMCL Channel 详情。</summary>
    public Task<HmclChannelDetailsDto> GetHmclChannelAsync(Guid channelId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<HmclChannelDetailsDto>($"api/v1/hmcl/channels/{channelId:D}", cancellationToken);
    }

    /// <summary>修改 HMCL Channel 元数据和保留策略。</summary>
    public Task<HmclChannelDto> UpdateHmclChannelAsync(
        Guid channelId,
        UpdateHmclChannelRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpdateHmclChannelRequest, HmclChannelDto>(
            HttpMethod.Patch, $"api/v1/hmcl/channels/{channelId:D}", request, cancellationToken);
    }

    /// <summary>异步删除 Channel，确认名必须完全匹配。</summary>
    public Task<OperationAcceptedDto> DeleteHmclChannelAsync(
        Guid channelId,
        ConfirmHmclChannelRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Delete, $"api/v1/hmcl/channels/{channelId:D}", request, cancellationToken);
    }

    /// <summary>切换公开或令牌可见性。</summary>
    public Task<HmclTokenResultDto> ChangeHmclChannelVisibilityAsync(
        Guid channelId,
        ChangeHmclVisibilityRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<ChangeHmclVisibilityRequest, HmclTokenResultDto>(
            HttpMethod.Post, $"api/v1/hmcl/channels/{channelId:D}/visibility", request, cancellationToken);
    }

    /// <summary>轮换私有 Channel 令牌；明文只在本响应返回。</summary>
    public Task<HmclTokenResultDto> RotateHmclChannelTokenAsync(
        Guid channelId,
        ConfirmHmclChannelRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<ConfirmHmclChannelRequest, HmclTokenResultDto>(
            HttpMethod.Post, $"api/v1/hmcl/channels/{channelId:D}/token/rotate", request, cancellationToken);
    }

    /// <summary>列出 Channel 的不可变 Release。</summary>
    public Task<CursorPage<HmclReleaseSummaryDto>> ListHmclReleasesAsync(
        Guid channelId, CancellationToken cancellationToken)
    {
        return ListHmclReleasesAsync(channelId, null, 50, cancellationToken);
    }

    /// <summary>分页列出 Channel 的不可变 Release。</summary>
    public Task<CursorPage<HmclReleaseSummaryDto>> ListHmclReleasesAsync(
        Guid channelId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<HmclReleaseSummaryDto>(
            $"api/v1/hmcl/channels/{channelId:D}/releases?cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>从停止中的实例采集内容创建 Release。</summary>
    public Task<OperationAcceptedDto> CreateHmclReleaseFromInstanceAsync(
        Guid channelId,
        CreateHmclReleaseFromInstanceRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, $"api/v1/hmcl/channels/{channelId:D}/releases", request, cancellationToken);
    }

    /// <summary>从已完成的可续传 Archive 创建 Release。</summary>
    public Task<OperationAcceptedDto> CreateHmclReleaseFromArchiveAsync(
        Guid channelId,
        CreateHmclReleaseFromArchiveRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, $"api/v1/hmcl/channels/{channelId:D}/releases", request, cancellationToken);
    }

    /// <summary>直接流式上传内容 ZIP 并创建 Release。</summary>
    public async Task<OperationAcceptedDto> CreateHmclReleaseFromStreamAsync(
        Guid channelId,
        Stream archive,
        string fileName,
        CreateHmclReleaseMetadataRequest metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(metadata);
        using MultipartFormDataContent form = new();
        using StreamContent file = new(archive);
        using StringContent metadataContent = new(JsonSerializer.Serialize(metadata, JsonOptions), Encoding.UTF8);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", Path.GetFileName(fileName));
        form.Add(metadataContent, "metadata");
        return await QueueContentAsync(
            HttpMethod.Post, $"api/v1/hmcl/channels/{channelId:D}/releases", form, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>预览发布差异和一次性门禁问题。</summary>
    public Task<HmclPublishDiffDto> PreviewHmclPublishAsync(
        Guid channelId,
        PublishHmclChannelRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<PublishHmclChannelRequest, HmclPublishDiffDto>(
            HttpMethod.Post, $"api/v1/hmcl/channels/{channelId:D}/preview", request, cancellationToken);
    }

    /// <summary>原子发布 Channel。</summary>
    public Task<OperationAcceptedDto> PublishHmclChannelAsync(
        Guid channelId,
        PublishHmclChannelRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, $"api/v1/hmcl/channels/{channelId:D}/publish", request, cancellationToken);
    }

    /// <summary>读取 HMCL 分发统计。</summary>
    public Task<CursorPage<HmclAccessStatDto>> ListHmclStatsAsync(
        Guid channelId, int days, CancellationToken cancellationToken)
    {
        return ListHmclStatsAsync(channelId, days, null, 50, cancellationToken);
    }

    /// <summary>分页读取 HMCL 分发统计。</summary>
    public Task<CursorPage<HmclAccessStatDto>> ListHmclStatsAsync(
        Guid channelId, int days, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<HmclAccessStatDto>(
            $"api/v1/hmcl/channels/{channelId:D}/stats?days={days.ToString(CultureInfo.InvariantCulture)}&cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>读取不可变 Release。</summary>
    public Task<HmclReleaseDto> GetHmclReleaseAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<HmclReleaseDto>($"api/v1/hmcl/releases/{releaseId:D}", cancellationToken);
    }

    /// <summary>分页读取 Release 文件。</summary>
    public Task<HmclReleaseFilePageDto> ListHmclReleaseFilesAsync(
        Guid releaseId,
        string? search,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        return GetJsonAsync<HmclReleaseFilePageDto>(
            $"api/v1/hmcl/releases/{releaseId:D}/files?search={Escape(search)}&cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>下载创建时固化的 Release manifest。</summary>
    public Task<DownloadedFileInfo> DownloadHmclReleaseManifestAsync(
        Guid releaseId, Stream destination, CancellationToken cancellationToken)
    {
        return DownloadToAsync(HttpMethod.Get, $"api/v1/hmcl/releases/{releaseId:D}/manifest", destination, cancellationToken);
    }

    /// <summary>把 Release 同步到停止中的实例。</summary>
    public Task<OperationAcceptedDto> SyncHmclReleaseAsync(
        Guid releaseId,
        SyncHmclReleaseToInstanceRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, $"api/v1/hmcl/releases/{releaseId:D}/sync", request, cancellationToken);
    }

    /// <summary>运行全部 Release 内容完整性检查。</summary>
    public Task<HmclIntegrityResultDto> RunHmclIntegrityAsync(CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<HmclIntegrityResultDto>(
            HttpMethod.Post, "api/v1/hmcl/integrity/run", cancellationToken);
    }

    /// <summary>创建可续传 HMCL Archive 会话。</summary>
    public Task<HmclArchiveDto> CreateHmclArchiveAsync(
        CreateHmclArchiveRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateHmclArchiveRequest, HmclArchiveDto>(
            HttpMethod.Post, "api/v1/hmcl/archives", request, cancellationToken);
    }

    /// <summary>读取 HMCL Archive 会话。</summary>
    public Task<HmclArchiveDto> GetHmclArchiveAsync(Guid archiveId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<HmclArchiveDto>($"api/v1/hmcl/archives/{archiveId:D}", cancellationToken);
    }

    /// <summary>流式上传 HMCL Archive 分块。</summary>
    public async Task<HmclArchiveDto> PutHmclArchivePartAsync(
        Guid archiveId,
        int index,
        string sha256,
        Stream content,
        long? contentLength,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentNullException.ThrowIfNull(content);
        using StreamContent body = new(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentLength = contentLength;
        return await SendContentAsync<HmclArchiveDto>(
            HttpMethod.Put,
            $"api/v1/hmcl/archives/{archiveId:D}/parts/{index.ToString(CultureInfo.InvariantCulture)}?sha256={Escape(sha256)}",
            body,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验并完成 HMCL Archive。</summary>
    public Task<HmclArchiveDto> CompleteHmclArchiveAsync(
        Guid archiveId,
        CompleteHmclArchiveRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<CompleteHmclArchiveRequest, HmclArchiveDto>(
            HttpMethod.Post, $"api/v1/hmcl/archives/{archiveId:D}/complete", request, cancellationToken);
    }

    /// <summary>删除 HMCL Archive 会话和暂存内容。</summary>
    public Task DeleteHmclArchiveAsync(Guid archiveId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/hmcl/archives/{archiveId:D}", cancellationToken);
    }

    /// <summary>流式上传并安全预览 Prism 原生实例 ZIP。</summary>
    public async Task<StagedPrismPreviewDto> PreviewPrismArchiveAsync(
        Stream archive,
        string fileName,
        Guid channelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        using MultipartFormDataContent form = new();
        using StreamContent file = new(archive);
        using StringContent channel = new(channelId.ToString("D"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", Path.GetFileName(fileName));
        form.Add(channel, "channelId");
        return await SendContentAsync<StagedPrismPreviewDto>(
            HttpMethod.Post, "api/v1/imports/prism/preview", form, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把已预览 Prism ZIP 转换为不可变 Release。</summary>
    public Task<OperationAcceptedDto> ConvertPrismArchiveAsync(
        Guid uploadId,
        ConvertStagedPrismRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, $"api/v1/imports/prism/{uploadId:D}/convert", request, cancellationToken);
    }
}

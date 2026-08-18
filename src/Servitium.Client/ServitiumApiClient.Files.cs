using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Files;
using Servitium.Contracts.Operations;

namespace Servitium.Client;

public sealed partial class ServitiumApiClient
{
    /// <summary>列出实例目录。</summary>
    public Task<FileEntryPageDto> ListFilesAsync(
        Guid instanceId,
        Guid nodeId,
        string? path,
        string? cursor,
        int pageSize,
        string? search,
        string sort,
        bool descending,
        CancellationToken cancellationToken)
    {
        return GetJsonAsync<FileEntryPageDto>(
            FilePath(instanceId, nodeId) +
            $"&path={Escape(path)}&cursor={Escape(cursor)}&pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}" +
            $"&search={Escape(search)}&sort={Escape(sort)}&descending={Bool(descending)}",
            cancellationToken);
    }

    /// <summary>读取实例文件系统容量。</summary>
    public Task<FileSystemStatusDto> GetFileSystemStatusAsync(Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<FileSystemStatusDto>(FilePath(instanceId, nodeId, "status"), cancellationToken);
    }

    /// <summary>创建目录。</summary>
    public Task<FileEntryDto> CreateDirectoryAsync(
        Guid instanceId, Guid nodeId, FilePathRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<FilePathRequest, FileEntryDto>(
            HttpMethod.Post, FilePath(instanceId, nodeId, "directories"), request, cancellationToken);
    }

    /// <summary>创建空文件。</summary>
    public Task<FileEntryDto> CreateFileAsync(
        Guid instanceId, Guid nodeId, FilePathRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<FilePathRequest, FileEntryDto>(
            HttpMethod.Post, FilePath(instanceId, nodeId, "files"), request, cancellationToken);
    }

    /// <summary>重命名文件或目录。</summary>
    public Task<FileEntryDto> RenameFileAsync(
        Guid instanceId, Guid nodeId, RenameFileRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<RenameFileRequest, FileEntryDto>(
            HttpMethod.Post, FilePath(instanceId, nodeId, "rename"), request, cancellationToken);
    }

    /// <summary>复制文件或目录。</summary>
    public Task<OperationAcceptedDto> CopyFilesAsync(
        Guid instanceId, Guid nodeId, FileTransferRequest request, CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, FilePath(instanceId, nodeId, "copy"), request, cancellationToken);
    }

    /// <summary>移动文件或目录。</summary>
    public Task<OperationAcceptedDto> MoveFilesAsync(
        Guid instanceId, Guid nodeId, FileTransferRequest request, CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, FilePath(instanceId, nodeId, "move"), request, cancellationToken);
    }

    /// <summary>批量删除相对路径。</summary>
    public Task<OperationAcceptedDto> DeleteFilesAsync(
        Guid instanceId, Guid nodeId, FileBatchRequest request, CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, FilePath(instanceId, nodeId, "delete"), request, cancellationToken);
    }

    /// <summary>创建 ZIP 归档。</summary>
    public Task<OperationAcceptedDto> CompressFilesAsync(
        Guid instanceId, Guid nodeId, CompressFilesRequest request, CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, FilePath(instanceId, nodeId, "compress"), request, cancellationToken);
    }

    /// <summary>安全解压归档。</summary>
    public Task<OperationAcceptedDto> ExtractArchiveAsync(
        Guid instanceId, Guid nodeId, ExtractArchiveRequest request, CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, FilePath(instanceId, nodeId, "extract"), request, cancellationToken);
    }

    /// <summary>经 SSRF 保护的 Server/Agent 链路下载 URL。</summary>
    public Task<OperationAcceptedDto> DownloadFileFromUrlAsync(
        Guid instanceId, Guid nodeId, DownloadFromUrlRequest request, CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, FilePath(instanceId, nodeId, "download-from-url"), request, cancellationToken);
    }

    /// <summary>把远端文件流式复制到目标流，支持可选字节范围。</summary>
    public async Task DownloadFileAsync(
        Guid instanceId,
        Guid nodeId,
        string path,
        Stream destination,
        long? offset,
        long? length,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(destination);
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get, FilePath(instanceId, nodeId, "content") + "&path=" + Escape(path), includeCsrf: false);
        if (offset is long start)
        {
            if (start < 0 || length is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            request.Headers.Range = new RangeHeaderValue(start, checked(start + length!.Value - 1));
        }
        else if (length is not null)
        {
            throw new ArgumentException("A length requires an offset.", nameof(length));
        }

        using HttpResponseMessage response = await SendReadRequestAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建可续传分块上传会话。</summary>
    public Task<UploadSessionDto> CreateFileUploadAsync(
        Guid instanceId,
        Guid nodeId,
        CreateUploadSessionRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateUploadSessionRequest, UploadSessionDto>(
            HttpMethod.Post, FilePath(instanceId, nodeId, "uploads"), request, cancellationToken);
    }

    /// <summary>读取上传会话和已接收分块。</summary>
    public Task<UploadSessionDto> GetFileUploadAsync(
        Guid instanceId, Guid nodeId, Guid uploadId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<UploadSessionDto>(FilePath(instanceId, nodeId, $"uploads/{uploadId:D}"), cancellationToken);
    }

    /// <summary>流式上传单个分块，Server 与 Agent 会独立校验 SHA-256。</summary>
    public async Task<UploadSessionDto> PutFileUploadPartAsync(
        Guid instanceId,
        Guid nodeId,
        Guid uploadId,
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
        return await SendContentAsync<UploadSessionDto>(
            HttpMethod.Put,
            FilePath(instanceId, nodeId, $"uploads/{uploadId:D}/parts/{index.ToString(CultureInfo.InvariantCulture)}") + "&sha256=" + Escape(sha256),
            body,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验并合并上传会话。</summary>
    public Task<OperationAcceptedDto> CompleteFileUploadAsync(
        Guid instanceId,
        Guid nodeId,
        Guid uploadId,
        CompleteUploadRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(
            HttpMethod.Post, FilePath(instanceId, nodeId, $"uploads/{uploadId:D}/complete"), request, cancellationToken);
    }

    /// <summary>取消上传并清理隔离分块。</summary>
    public Task CancelFileUploadAsync(
        Guid instanceId, Guid nodeId, Guid uploadId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, FilePath(instanceId, nodeId, $"uploads/{uploadId:D}"), cancellationToken);
    }

    /// <summary>读取受大小限制的文本文件。</summary>
    public Task<TextFileDto> ReadTextFileAsync(
        Guid instanceId,
        Guid nodeId,
        string path,
        string? encoding,
        CancellationToken cancellationToken)
    {
        return GetJsonAsync<TextFileDto>(
            FilePath(instanceId, nodeId, "text") + $"&path={Escape(path)}&encoding={Escape(encoding)}",
            cancellationToken);
    }

    /// <summary>写入文本文件并保留显式编码。</summary>
    public Task<TextFileDto> WriteTextFileAsync(
        Guid instanceId,
        Guid nodeId,
        string path,
        WriteTextFileRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<WriteTextFileRequest, TextFileDto>(
            HttpMethod.Put, FilePath(instanceId, nodeId, "text") + "&path=" + Escape(path), request, cancellationToken);
    }

    private static string FilePath(Guid instanceId, Guid nodeId, string? suffix = null)
    {
        string route = $"api/v1/instances/{instanceId:D}/files";
        if (!string.IsNullOrEmpty(suffix))
        {
            route += "/" + suffix;
        }

        return route + "?nodeId=" + nodeId.ToString("D", CultureInfo.InvariantCulture);
    }
}

using System.Collections.Generic;

using Microsoft.AspNetCore.Http;

namespace Servitium.Server.OpenApi;

/// <summary>仅供 OpenAPI 表达单文件 multipart 请求。</summary>
internal sealed record SingleFileUploadRequest(IFormFile File);

internal sealed record PrismArchivePreviewUploadRequest(IFormFile File, System.Guid? ChannelId);

/// <summary>仅供 OpenAPI 表达服务端归档导入 multipart 请求。</summary>
internal sealed record ServerArchiveUploadRequest(
    IFormFile File,
    System.Guid NodeId,
    string Name,
    string Launch,
    string StopCommand = "stop",
    int StopTimeoutSeconds = 60,
    IReadOnlyList<string>? Tags = null,
    string? Notes = null);

/// <summary>仅供 OpenAPI 表达 Release 归档上传 multipart 请求。</summary>
internal sealed record ReleaseArchiveUploadRequest(IFormFile File, string Metadata);

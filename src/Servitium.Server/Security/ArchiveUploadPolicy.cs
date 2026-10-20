using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace Servitium.Server.Security;

/// <summary>归档上传在 HTTP 边界使用的硬限制。</summary>
internal static class ArchiveUploadPolicy
{
    /// <summary>压缩归档本身的最大长度，与默认 8 GiB 解压上限保持同一数量级。</summary>
    internal const long MaximumArchiveFileBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>为 multipart 边界、文件名和元数据保留的固定开销。</summary>
    private const long MaximumMultipartOverheadBytes = 16L * 1024 * 1024;

    /// <summary>包含 multipart 开销的请求体上限。</summary>
    internal const long MaximumRequestBodyBytes = MaximumArchiveFileBytes + MaximumMultipartOverheadBytes;

    /// <summary>附加到三个归档上传端点的请求体限制元数据。</summary>
    internal static IRequestSizeLimitMetadata RequestSizeLimit { get; } = new RequestBodySizeLimitMetadata(MaximumRequestBodyBytes);

    /// <summary>判断已解析的单个归档文件是否超过硬限制。</summary>
    internal static bool ExceedsFileLimit(IFormFile file)
    {
        return file.Length > MaximumArchiveFileBytes;
    }
}

/// <summary>供最小 API 端点声明请求体上限的元数据。</summary>
internal sealed record RequestBodySizeLimitMetadata(long? MaxRequestBodySize) : IRequestSizeLimitMetadata;

/// <summary>在读取请求体前把端点元数据应用到 Kestrel 限制特性。</summary>
internal sealed class RequestBodySizeLimitMiddleware(RequestDelegate next)
{
    internal async Task InvokeAsync(HttpContext context)
    {
        IRequestSizeLimitMetadata? metadata = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (metadata is not null && feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = metadata.MaxRequestBodySize;
        }

        await next(context).ConfigureAwait(false);
    }
}

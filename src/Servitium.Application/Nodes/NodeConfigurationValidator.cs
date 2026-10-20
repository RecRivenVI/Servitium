using System;
using System.Linq;

using Servitium.Contracts.Nodes;
using Servitium.Domain.Primitives;

namespace Servitium.Application.Nodes;

/// <summary>节点配置中与本地文件系统无关的共享约束。</summary>
public static class NodeConfigurationValidator
{
    /// <summary>验证配置大小、范围与文本边界。</summary>
    public static void Validate(UpdateNodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64 || request.Name.IndexOfAny(['\r', '\n']) >= 0 ||
            request.Notes?.Length > 4096 || request.Tags?.Count > 32 || request.Tags?.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag.Any(char.IsControl)) == true ||
            request.TransferBytesPerSecond < 0 || request.UploadBytesPerSecond < 0 || request.DownloadBytesPerSecond < 0 ||
            request.UrlDownloadConcurrency is < 1 or > 32 || request.FileTaskConcurrency is < 1 or > 32 || request.CompressionTaskConcurrency is < 1 or > 8 ||
            request.TerminalBufferBytes is < 65_536 or > 16_777_216 || request.TerminalBufferLines is < 100 or > 50_000 ||
            request.ShutdownWaitSeconds is < 5 or > 600 || request.MetricIntervalSeconds is < 1 or > 60 || request.MaximumSchedulesPerInstance is < 1 or > 128 ||
            (request.PublicAddress is { Length: > 0 } address && (address.Length > 512 || address.Any(char.IsControl))) ||
            request.PrivateSourceAuthorities?.Count > 64 ||
            request.PrivateSourceAuthorities?.Any(authority =>
                string.IsNullOrWhiteSpace(authority) || authority.Length > 253 ||
                !Uri.TryCreate("https://" + authority, UriKind.Absolute, out Uri? parsed) ||
                !string.Equals(parsed.Authority, authority, StringComparison.OrdinalIgnoreCase)) == true ||
            request.AddressMappings?.Count > 32 || request.AddressMappings?.Any(mapping =>
                string.IsNullOrWhiteSpace(mapping.InternalAddress) || mapping.InternalAddress.Length > 512 || mapping.InternalAddress.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(mapping.PublicAddress) || mapping.PublicAddress.Length > 512 || mapping.PublicAddress.Any(char.IsControl)) == true)
        {
            throw new DomainException("validation-failed", "The node configuration is invalid.");
        }
    }
}

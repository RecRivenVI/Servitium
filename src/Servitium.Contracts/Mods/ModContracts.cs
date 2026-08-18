namespace Servitium.Contracts.Mods;

/// <summary>模组或插件摘要。</summary>
/// <param name="RelativePath">相对路径。</param>
/// <param name="Name">名称。</param>
/// <param name="Enabled">是否启用。</param>
public sealed record ModDto(string RelativePath, string Name, bool Enabled);

/// <summary>模组或插件元数据。</summary>
/// <param name="Id">以规范化相对路径派生的稳定标识。</param>
/// <param name="RelativePath">相对路径。</param>
/// <param name="Name">显示名。</param>
/// <param name="Version">版本。</param>
/// <param name="Description">说明。</param>
/// <param name="Authors">作者。</param>
/// <param name="Loader">加载器或插件平台。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Size">文件大小。</param>
/// <param name="ModifiedAt">最后修改时间。</param>
/// <param name="Dependencies">声明的依赖标识。</param>
/// <param name="MetadataUnreadable">元数据是否不可读取。</param>
/// <param name="Identifier">声明的模组或插件稳定标识。</param>
public sealed record ModDetailsDto(
    string Id,
    string RelativePath,
    string Name,
    string? Version,
    string? Description,
    System.Collections.Generic.IReadOnlyList<string> Authors,
    string? Loader,
    bool Enabled,
    long Size,
    System.DateTimeOffset ModifiedAt,
    System.Collections.Generic.IReadOnlyList<string>? Dependencies,
    bool MetadataUnreadable,
    string? Identifier);

/// <summary>模组上传完成请求。</summary>
/// <param name="UploadId">已完成的分片上传会话。</param>
/// <param name="Kind">`mods` 或 `plugins`。</param>
/// <param name="Sha256">完整 JAR 的 SHA-256。</param>
public sealed record UploadModRequest(System.Guid UploadId, string Kind, string Sha256);

/// <summary>设置模组或插件启用状态。</summary>
/// <param name="Enabled">是否启用。</param>
public sealed record SetModEnabledRequest(bool Enabled);

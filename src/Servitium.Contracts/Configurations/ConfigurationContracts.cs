namespace Servitium.Contracts.Configurations;

/// <summary>配置文件摘要。</summary>
/// <param name="Key">稳定键。</param>
/// <param name="RelativePath">相对路径。</param>
/// <param name="Format">格式。</param>
/// <param name="HasConflict">同一逻辑配置是否存在多个候选。</param>
/// <param name="Candidates">全部候选相对路径。</param>
/// <param name="ModifiedAt">当前候选最后修改时间。</param>
public sealed record ConfigurationDto(
    string Key,
    string RelativePath,
    string Format,
    bool HasConflict,
    System.Collections.Generic.IReadOnlyList<string>? Candidates,
    System.DateTimeOffset ModifiedAt);

/// <summary>结构化配置详情。</summary>
/// <param name="Key">稳定键。</param>
/// <param name="RelativePath">相对路径。</param>
/// <param name="Format">格式。</param>
/// <param name="Values">扁平化键值。</param>
/// <param name="RawText">原始文本。</param>
/// <param name="Revision">内容修订哈希。</param>
/// <param name="ModifiedAt">修改时间。</param>
/// <param name="Encoding">检测到的文本编码。</param>
/// <param name="LineEnding">行尾模式。</param>
/// <param name="UnknownKeys">不在内置字段定义中的键。</param>
/// <param name="Diagnostics">语法与识别诊断。</param>
public sealed record ConfigurationDetailsDto(
    string Key,
    string RelativePath,
    string Format,
    System.Collections.Generic.IReadOnlyDictionary<string, string?> Values,
    string RawText,
    string Revision,
    System.DateTimeOffset ModifiedAt,
    string Encoding = "utf-8",
    string LineEnding = "none",
    System.Collections.Generic.IReadOnlyList<string>? UnknownKeys = null,
    System.Collections.Generic.IReadOnlyList<string>? Diagnostics = null);

/// <summary>结构化配置写入。</summary>
/// <param name="Values">要更新的键值。</param>
/// <param name="RawText">原始文本模式内容；与 Values 二选一。</param>
/// <param name="ExpectedRevision">读取时的修订，用于并发冲突检测。</param>
public sealed record WriteConfigurationRequest(
    System.Collections.Generic.IReadOnlyDictionary<string, string?>? Values,
    string? RawText,
    string ExpectedRevision);

/// <summary>配置备份。</summary>
/// <param name="Id">备份标识。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="Size">大小。</param>
/// <param name="Revision">内容修订。</param>
public sealed record ConfigurationBackupDto(System.Guid Id, System.DateTimeOffset CreatedAt, long Size, string Revision);

/// <summary>配置字段定义。</summary>
/// <param name="Key">字段键。</param>
/// <param name="Type">类型。</param>
/// <param name="Required">是否必填。</param>
/// <param name="DescriptionKey">说明文案键。</param>
/// <param name="DefaultValue">默认值。</param>
/// <param name="Minimum">可选最小值。</param>
/// <param name="Maximum">可选最大值。</param>
/// <param name="Unit">可选单位。</param>
/// <param name="Group">字段分组。</param>
/// <param name="AllowedValues">枚举允许值。</param>
public sealed record ConfigurationFieldDto(
    string Key,
    string Type,
    bool Required,
    string DescriptionKey,
    string? DefaultValue = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    string? Unit = null,
    string Group = "other",
    System.Collections.Generic.IReadOnlyList<string>? AllowedValues = null);

/// <summary>随协议分发的 server.properties 字段定义。</summary>
public static class ServerPropertiesSchema
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>随程序集分发的数据文件中声明的结构化字段。</summary>
    public static System.Collections.Generic.IReadOnlyList<ConfigurationFieldDto> Fields { get; } = Load();

    private static System.Collections.Generic.List<ConfigurationFieldDto> Load()
    {
        using System.IO.Stream stream = typeof(ServerPropertiesSchema).Assembly.GetManifestResourceStream(
            "Servitium.Contracts.Resources.server-properties-schema.json")
            ?? throw new System.InvalidOperationException("The embedded server.properties schema is missing.");
        return System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<ConfigurationFieldDto>>(
            stream,
            SerializerOptions)
            ?? throw new System.InvalidOperationException("The embedded server.properties schema is invalid.");
    }
}

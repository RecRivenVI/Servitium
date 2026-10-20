using System;

namespace Servitium.Contracts.Java;

/// <summary>Java 运行时。</summary>
/// <param name="Id">运行时标识。</param>
/// <param name="NodeId">节点标识。</param>
/// <param name="ExecutablePath">java.exe 路径。</param>
/// <param name="Version">版本。</param>
/// <param name="Vendor">厂商。</param>
/// <param name="Architecture">体系结构。</param>
/// <param name="Source">来源。</param>
/// <param name="DisplayName">可读名称。</param>
/// <param name="MajorVersion">主版本号。</param>
/// <param name="Imported">是否拥有导入文件。</param>
/// <param name="RegisteredAt">登记时间。</param>
/// <param name="VmName">VM 名称。</param>
/// <param name="UsedByInstanceCount">使用中的实例数。</param>
public sealed record JavaRuntimeDto(
    Guid Id,
    Guid NodeId,
    string ExecutablePath,
    string Version,
    string Vendor,
    string Architecture,
    string Source,
    string? DisplayName,
    int? MajorVersion,
    bool Imported,
    DateTimeOffset RegisteredAt,
    string? VmName,
    int UsedByInstanceCount);

/// <summary>手工登记节点 Java 运行时。</summary>
/// <param name="ExecutablePath">节点本地 java.exe 绝对路径。</param>
public sealed record RegisterJavaRuntimeRequest(string ExecutablePath);

/// <summary>Java 运行时详情及使用它的实例。</summary>
public sealed record JavaRuntimeDetailsDto(
    JavaRuntimeDto Runtime,
    System.Collections.Generic.IReadOnlyList<Guid> UsedByInstances);

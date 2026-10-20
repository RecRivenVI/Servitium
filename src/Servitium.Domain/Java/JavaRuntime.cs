using System;

using Servitium.Domain.Primitives;

namespace Servitium.Domain.Java;

/// <summary>定义 Java 运行时来源。</summary>
public enum JavaRuntimeSource
{
    /// <summary>自动发现。</summary>
    Discovered,
    /// <summary>手工登记。</summary>
    Manual,
    /// <summary>由 Servitium 导入。</summary>
    Imported,
}

/// <summary>定义支持的 Java 体系结构。</summary>
public enum JavaArchitecture
{
    /// <summary>x64。</summary>
    X64,
    /// <summary>arm64。</summary>
    Arm64,
}

/// <summary>表示已登记 Java 运行时。</summary>
public sealed class JavaRuntime : Entity
{
    /// <summary>初始化 Java 运行时。</summary>
    public JavaRuntime(
        Guid id,
        Guid nodeId,
        string executablePath,
        string version,
        string vendor,
        JavaArchitecture architecture,
        JavaRuntimeSource source)
        : base(id)
    {
        _ = Guard.AgainstEmpty(nodeId, nameof(nodeId));
        NodeId = nodeId;
        ExecutablePath = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(executablePath, nameof(executablePath)),
            nameof(executablePath));
        Version = Guard.AgainstNullOrWhiteSpace(version, nameof(version));
        Vendor = Guard.AgainstNullOrWhiteSpace(vendor, nameof(vendor));
        Architecture = architecture;
        Source = source;
    }

    /// <summary>获取节点标识。</summary>
    public Guid NodeId { get; }

    /// <summary>获取 java.exe 的绝对路径。</summary>
    public string ExecutablePath { get; }

    /// <summary>获取版本。</summary>
    public string Version { get; }

    /// <summary>获取厂商。</summary>
    public string Vendor { get; }

    /// <summary>获取体系结构。</summary>
    public JavaArchitecture Architecture { get; }

    /// <summary>获取来源。</summary>
    public JavaRuntimeSource Source { get; }

    /// <summary>获取是否允许删除运行时文件。</summary>
    public bool CanDeleteFiles => Source == JavaRuntimeSource.Imported;

    /// <summary>确保运行时文件可由 Servitium 删除。</summary>
    public void EnsureFilesCanBeDeleted()
    {
        if (!CanDeleteFiles)
        {
            throw new DomainException("java-runtime-not-imported", "Only imported Java runtime files can be deleted.");
        }
    }
}

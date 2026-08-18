using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

using Servitium.Domain.Common;

namespace Servitium.Domain.Distribution;

/// <summary>定义 Channel 可见性。</summary>
public enum ChannelVisibility
{
    /// <summary>公开访问。</summary>
    Public,
    /// <summary>需要令牌。</summary>
    TokenProtected,
}

/// <summary>定义受支持的单一模组加载器。</summary>
public enum ModLoaderKind
{
    /// <summary>Forge。</summary>
    Forge,
    /// <summary>NeoForge。</summary>
    NeoForge,
    /// <summary>Fabric。</summary>
    Fabric,
    /// <summary>Quilt。</summary>
    Quilt,
}

/// <summary>表示 SHA-1 内容哈希。</summary>
public sealed record ContentHash : ValueObject
{
    private static readonly Regex Sha1Pattern = new(
        "^[0-9a-f]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>初始化内容哈希。</summary>
    public ContentHash(string value)
    {
        string normalized = Guard.AgainstNullOrWhiteSpace(value, nameof(value));
        if (!Sha1Pattern.IsMatch(normalized))
        {
            throw new DomainException("hmcl-release-validation-failed", "A content hash must be 40 lowercase hexadecimal characters.");
        }

        Value = normalized;
    }

    /// <summary>获取小写 SHA-1。</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }
}

/// <summary>表示 Release 的不可变文件条目。</summary>
public sealed record HmclReleaseFile : ValueObject
{
    /// <summary>初始化文件条目。</summary>
    public HmclReleaseFile(string path, ContentHash hash, long size)
    {
        string normalized = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(path, nameof(path)),
            nameof(path)).Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new DomainException("hmcl-release-validation-failed", "A release file path must be a normalized relative path.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(size);

        Path = normalized;
        Hash = hash ?? throw new ArgumentNullException(nameof(hash));
        Size = size;
    }

    /// <summary>获取正斜杠相对路径。</summary>
    public string Path { get; }

    /// <summary>获取内容哈希。</summary>
    public ContentHash Hash { get; }

    /// <summary>获取文件大小。</summary>
    public long Size { get; }
}

/// <summary>表示单一加载器与可选 OptiFine 声明。</summary>
/// <param name="ModLoader">模组加载器。</param>
/// <param name="ModLoaderVersion">模组加载器版本。</param>
/// <param name="OptiFineVersion">可选 OptiFine 版本。</param>
public sealed record LoaderDeclaration(ModLoaderKind? ModLoader, string? ModLoaderVersion, string? OptiFineVersion) : ValueObject
{
    /// <summary>获取无加载器声明。</summary>
    public static LoaderDeclaration None { get; } = new(null, null, null);
}

/// <summary>表示创建后不可变的 HMCL Release。</summary>
public sealed class HmclRelease : Entity
{
    private readonly ReadOnlyCollection<HmclReleaseFile> _files;

    /// <summary>初始化 Release。</summary>
    public HmclRelease(
        Guid id,
        Guid channelId,
        long sequence,
        string name,
        string author,
        string version,
        string description,
        string gameVersion,
        LoaderDeclaration loader,
        IEnumerable<HmclReleaseFile> files,
        DateTimeOffset createdAt,
        Guid createdBy)
        : base(id)
    {
        ChannelId = Guard.AgainstEmpty(channelId, nameof(channelId));
        CreatedBy = Guard.AgainstEmpty(createdBy, nameof(createdBy));
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);

        Sequence = sequence;
        Name = Guard.AgainstNullOrWhiteSpace(name, nameof(name));
        Author = Guard.AgainstNullOrWhiteSpace(author, nameof(author));
        Version = Guard.AgainstNullOrWhiteSpace(version, nameof(version));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        GameVersion = Guard.AgainstNullOrWhiteSpace(gameVersion, nameof(gameVersion));
        Loader = loader ?? throw new ArgumentNullException(nameof(loader));
        if ((loader.ModLoader is null) != string.IsNullOrWhiteSpace(loader.ModLoaderVersion))
        {
            throw new DomainException("hmcl-release-validation-failed", "A mod loader and its version must be declared together.");
        }

        ArgumentNullException.ThrowIfNull(files);
        HmclReleaseFile[] fileArray = [.. files];
        if (fileArray.Length == 0)
        {
            throw new DomainException("hmcl-release-validation-failed", "A release must contain at least one file.");
        }

        if (fileArray.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fileArray.Length)
        {
            throw new DomainException("hmcl-release-validation-failed", "Release file paths must be unique ignoring case.");
        }

        _files = Array.AsReadOnly(fileArray);
        CreatedAt = Guard.AsUtc(createdAt);
        FileCount = fileArray.Length;
        TotalSize = fileArray.Sum(file => file.Size);
    }

    /// <summary>获取 Channel 标识。</summary>
    public Guid ChannelId { get; }

    /// <summary>获取 Channel 内序号。</summary>
    public long Sequence { get; }

    /// <summary>获取名称。</summary>
    public string Name { get; }

    /// <summary>获取作者。</summary>
    public string Author { get; }

    /// <summary>获取版本。</summary>
    public string Version { get; }

    /// <summary>获取描述。</summary>
    public string Description { get; }

    /// <summary>获取游戏版本。</summary>
    public string GameVersion { get; }

    /// <summary>获取加载器声明。</summary>
    public LoaderDeclaration Loader { get; }

    /// <summary>获取文件清单。</summary>
    public IReadOnlyList<HmclReleaseFile> Files => _files;

    /// <summary>获取创建时间。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>获取创建者。</summary>
    public Guid CreatedBy { get; }

    /// <summary>获取文件数。</summary>
    public int FileCount { get; }

    /// <summary>获取总大小。</summary>
    public long TotalSize { get; }
}

/// <summary>表示稳定 slug 的 HMCL Channel。</summary>
public sealed class HmclChannel : Entity
{
    private static readonly Regex SlugPattern = new(
        "^[a-z0-9-]{3,48}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>初始化 Channel。</summary>
    public HmclChannel(Guid id, string slug, string name, ChannelVisibility visibility, DateTimeOffset createdAt)
        : base(id)
    {
        if (!SlugPattern.IsMatch(slug))
        {
            throw new DomainException("validation-failed", "The channel slug is invalid.");
        }

        Slug = slug;
        Name = Guard.AgainstNullOrWhiteSpace(name, nameof(name));
        Visibility = visibility;
        CreatedAt = Guard.AsUtc(createdAt);
    }

    /// <summary>获取不可变 slug。</summary>
    public string Slug { get; }

    /// <summary>获取显示名。</summary>
    public string Name { get; private set; }

    /// <summary>获取可见性。</summary>
    public ChannelVisibility Visibility { get; private set; }

    /// <summary>获取创建时间。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>获取当前生效 Release。</summary>
    public Guid? ActiveReleaseId { get; private set; }

    /// <summary>原子更新内存模型中的生效指针。</summary>
    public void Publish(Guid releaseId)
    {
        ActiveReleaseId = Guard.AgainstEmpty(releaseId, nameof(releaseId));
    }
}

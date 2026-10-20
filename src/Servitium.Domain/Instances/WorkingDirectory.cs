using System.Text.RegularExpressions;

using Servitium.Domain.Primitives;

namespace Servitium.Domain.Instances;

/// <summary>
/// 表示实例绝对工作目录及其归属。
/// </summary>
public sealed record WorkingDirectory : ValueObject
{
    private static readonly Regex DrivePathPattern = new(
        "^[A-Za-z]:\\\\(?:[^\\x00-\\x1F<>:\"|?*\\\\]+\\\\?)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex UncPathPattern = new(
        "^\\\\\\\\[^\\x00-\\x1F<>:\"|?*\\\\]+\\\\[^\\x00-\\x1F<>:\"|?*\\\\]+(?:\\\\[^\\x00-\\x1F<>:\"|?*\\\\]+)*\\\\?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>初始化工作目录。</summary>
    public WorkingDirectory(string absolutePath, bool ownedByServitium)
    {
        string normalized = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(absolutePath, nameof(absolutePath)),
            nameof(absolutePath));
        if (!DrivePathPattern.IsMatch(normalized) && !UncPathPattern.IsMatch(normalized))
        {
            throw new DomainException("instance-path-invalid", "The working directory must be an absolute Windows path.");
        }

        AbsolutePath = normalized.TrimEnd('\\');
        OwnedByServitium = ownedByServitium;
    }

    /// <summary>获取绝对路径。</summary>
    public string AbsolutePath { get; }

    /// <summary>获取目录是否由 Servitium 创建和拥有。</summary>
    public bool OwnedByServitium { get; }

    /// <summary>获取是否允许进入递归删除安全校验。</summary>
    public bool CanDeleteRecursively => OwnedByServitium;
}

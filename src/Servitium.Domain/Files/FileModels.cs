using Servitium.Domain.Common;

namespace Servitium.Domain.Files;

/// <summary>表示经过形态校验的文件路径。</summary>
public sealed record FilePath : ValueObject
{
    /// <summary>初始化文件路径。</summary>
    public FilePath(string value)
    {
        Value = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(value, nameof(value)),
            nameof(value));
    }

    /// <summary>获取原始路径值。</summary>
    public string Value { get; }
}

/// <summary>表示路径校验结果。</summary>
/// <param name="IsValid">是否有效。</param>
/// <param name="NormalizedPath">规范化路径。</param>
/// <param name="ErrorCode">失败错误码。</param>
public sealed record PathValidationResult(bool IsValid, string? NormalizedPath, string? ErrorCode) : ValueObject
{
    /// <summary>创建成功结果。</summary>
    public static PathValidationResult Success(string normalizedPath)
    {
        return new(true, normalizedPath, null);
    }

    /// <summary>创建失败结果。</summary>
    public static PathValidationResult Failure(string errorCode)
    {
        return new(false, null, errorCode);
    }
}

using System;

namespace Servitium.Domain.Primitives;

/// <summary>
/// 提供领域构造边界使用的基础校验。
/// </summary>
public static class Guard
{
    /// <summary>
    /// 拒绝空标识。
    /// </summary>
    public static Guid AgainstEmpty(Guid value, string parameterName)
    {
        return value == Guid.Empty
            ? throw new ArgumentException("The identifier cannot be empty.", parameterName)
            : value;
    }

    /// <summary>
    /// 拒绝空白文本。
    /// </summary>
    public static string AgainstNullOrWhiteSpace(string? value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("The value cannot be empty.", parameterName)
            : value.Trim();
    }

    /// <summary>
    /// 拒绝含控制字符的文本。
    /// </summary>
    public static string AgainstControlCharacters(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("The value cannot contain control characters.", parameterName);
            }
        }

        return value;
    }

    /// <summary>
    /// 把时间规范为 UTC。
    /// </summary>
    public static DateTimeOffset AsUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime();
    }
}

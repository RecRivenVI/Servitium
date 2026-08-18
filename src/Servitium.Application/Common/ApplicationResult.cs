using System.Collections.Generic;

namespace Servitium.Application.Common;

/// <summary>表示不依赖本地化文案的用例结果。</summary>
/// <typeparam name="T">成功值类型。</typeparam>
public sealed class ApplicationResult<T>
{
    internal ApplicationResult(bool succeeded, T? value, string? errorCode, IReadOnlyDictionary<string, string>? parameters)
    {
        Succeeded = succeeded;
        Value = value;
        ErrorCode = errorCode;
        Parameters = parameters ?? new Dictionary<string, string>();
    }

    /// <summary>获取是否成功。</summary>
    public bool Succeeded { get; }

    /// <summary>获取成功值。</summary>
    public T? Value { get; }

    /// <summary>获取稳定错误码。</summary>
    public string? ErrorCode { get; }

    /// <summary>获取错误参数。</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

}

/// <summary>创建强类型用例结果。</summary>
public static class ApplicationResult
{
    /// <summary>创建成功结果。</summary>
    public static ApplicationResult<T> Success<T>(T value)
    {
        return new(true, value, null, null);
    }

    /// <summary>创建失败结果。</summary>
    public static ApplicationResult<T> Failure<T>(string errorCode, IReadOnlyDictionary<string, string>? parameters = null)
    {
        return new(false, default, errorCode, parameters);
    }
}

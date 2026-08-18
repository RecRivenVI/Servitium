using System;

namespace Servitium.Domain.Common;

/// <summary>
/// 表示携带稳定错误码的领域规则异常。
/// </summary>
/// <remarks>
/// 初始化领域异常。
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1032:实现标准异常构造函数",
    Justification = "领域异常必须由调用方显式提供稳定错误码，缺码构造函数会破坏协议分支。")]
public class DomainException(string code, string message) : InvalidOperationException(message)
{

    /// <summary>
    /// 获取稳定错误码。
    /// </summary>
    public string Code { get; } = Guard.AgainstNullOrWhiteSpace(code, nameof(code));
}

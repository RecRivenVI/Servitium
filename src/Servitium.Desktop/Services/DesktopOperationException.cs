using System;
using System.Diagnostics.CodeAnalysis;

namespace Servitium.Desktop.Services;

[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "此内部异常只携带服务端长操作的稳定错误码，调用方必须提供错误码。")]
internal sealed class DesktopOperationException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

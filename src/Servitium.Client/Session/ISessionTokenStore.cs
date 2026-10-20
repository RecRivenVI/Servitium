using System.Threading;
using System.Threading.Tasks;

namespace Servitium.Client.Session;

/// <summary>Desktop 提供的受保护会话令牌存储；Client 不规定具体平台实现。</summary>
public interface ISessionTokenStore
{
    /// <summary>读取当前服务器的会话令牌。</summary>
    Task<string?> ReadAsync(string serverKey, CancellationToken cancellationToken);

    /// <summary>保存或清除当前服务器的会话令牌。</summary>
    Task WriteAsync(string serverKey, string? token, CancellationToken cancellationToken);
}

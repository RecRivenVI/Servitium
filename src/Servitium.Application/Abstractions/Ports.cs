using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Domain.Auditing;
using Servitium.Domain.Distribution;
using Servitium.Domain.Files;
using Servitium.Domain.Identity;
using Servitium.Domain.Instances;
using Servitium.Domain.Java;
using Servitium.Domain.Nodes;
using Servitium.Domain.Operations;
using Servitium.Domain.Scheduling;

namespace Servitium.Application.Abstractions;

/// <summary>提供当前 UTC 时间。</summary>
public interface IClock
{
    /// <summary>获取当前 UTC 时间。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>生成版本 4 Guid。</summary>
public interface IGuidFactory
{
    /// <summary>创建标识。</summary>
    Guid Create();
}

/// <summary>密码哈希端口。</summary>
public interface IPasswordHasher
{
    /// <summary>生成包含参数的密码哈希。</summary>
    string Hash(string password);
    /// <summary>验证密码。</summary>
    bool Verify(string password, string encodedHash);
    /// <summary>判断已验证的哈希是否应按当前参数重建。</summary>
    bool NeedsRehash(string encodedHash)
    {
        return false;
    }
}

/// <summary>TOTP 端口。</summary>
public interface ITotpService
{
    /// <summary>生成共享密钥。</summary>
    string GenerateSecret();
    /// <summary>验证验证码。</summary>
    bool Verify(string secret, string code, DateTimeOffset now);
}

/// <summary>秘密保护端口。</summary>
public interface ISecretProtector
{
    /// <summary>保护明文。</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    /// <summary>解开密文。</summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

/// <summary>高熵令牌生成端口。</summary>
public interface ITokenGenerator
{
    /// <summary>生成指定字节熵的令牌。</summary>
    string Generate(int entropyBytes);
}

/// <summary>事务边界。</summary>
public interface IUnitOfWork
{
    /// <summary>提交当前事务。</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>用户仓储。</summary>
public interface IUserRepository
{
    /// <summary>按标识读取。</summary>
    Task<User?> FindAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>按用户名读取。</summary>
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken);
    /// <summary>添加用户。</summary>
    Task AddAsync(User user, CancellationToken cancellationToken);
}

/// <summary>节点仓储。</summary>
public interface INodeRepository
{
    /// <summary>按标识读取。</summary>
    Task<Node?> FindAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>添加节点。</summary>
    Task AddAsync(Node node, CancellationToken cancellationToken);
}

/// <summary>实例仓储。</summary>
public interface IInstanceRepository
{
    /// <summary>按标识读取。</summary>
    Task<Instance?> FindAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>添加实例镜像。</summary>
    Task AddAsync(Instance instance, CancellationToken cancellationToken);
}

/// <summary>Java 运行时仓储。</summary>
public interface IJavaRuntimeRepository
{
    /// <summary>列出节点运行时。</summary>
    Task<IReadOnlyList<JavaRuntime>> ListByNodeAsync(Guid nodeId, CancellationToken cancellationToken);
}

/// <summary>计划任务仓储。</summary>
public interface IScheduledTaskRepository
{
    /// <summary>列出实例任务。</summary>
    Task<IReadOnlyList<ScheduledTask>> ListByInstanceAsync(Guid instanceId, CancellationToken cancellationToken);
}

/// <summary>长操作仓储。</summary>
public interface IOperationRepository
{
    /// <summary>按标识读取。</summary>
    Task<Operation?> FindAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>添加操作。</summary>
    Task AddAsync(Operation operation, CancellationToken cancellationToken);
}

/// <summary>HMCL Channel 仓储。</summary>
public interface IHmclChannelRepository
{
    /// <summary>按标识读取。</summary>
    Task<HmclChannel?> FindAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>按 slug 读取。</summary>
    Task<HmclChannel?> FindBySlugAsync(string slug, CancellationToken cancellationToken);
    /// <summary>添加 Channel。</summary>
    Task AddAsync(HmclChannel channel, CancellationToken cancellationToken);
}

/// <summary>HMCL Release 仓储。</summary>
public interface IHmclReleaseRepository
{
    /// <summary>按标识读取。</summary>
    Task<HmclRelease?> FindAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>添加不可变 Release。</summary>
    Task AddAsync(HmclRelease release, CancellationToken cancellationToken);
}

/// <summary>Agent 指令与子流网关。</summary>
public interface IAgentGateway
{
    /// <summary>发送结构化指令。</summary>
    Task<AgentCommandResult> SendAsync(Guid nodeId, AgentCommand command, CancellationToken cancellationToken);
}

/// <summary>Agent 指令。</summary>
/// <param name="Type">协议指令类型。</param>
/// <param name="Payload">序列化载荷。</param>
public sealed record AgentCommand(string Type, ReadOnlyMemory<byte> Payload);

/// <summary>Agent 指令结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Payload">响应载荷。</param>
/// <param name="ErrorCode">稳定错误码。</param>
public sealed record AgentCommandResult(bool Succeeded, ReadOnlyMemory<byte> Payload, string? ErrorCode);

/// <summary>实时事件发布端口。</summary>
public interface IEventPublisher
{
    /// <summary>发布事件信封。</summary>
    Task PublishAsync<TPayload>(long eventId, string type, string scope, TPayload payload, CancellationToken cancellationToken);
}

/// <summary>长操作跟踪端口。</summary>
public interface IOperationTracker
{
    /// <summary>建立并持久化长操作。</summary>
    Task<Operation> CreateAsync(OperationKind kind, string target, CancellationToken cancellationToken);
    /// <summary>建立并持久化带发起用户的长操作。</summary>
    Task<Operation> CreateAsync(OperationKind kind, string target, Guid initiatedBy, CancellationToken cancellationToken)
    {
        return CreateAsync(kind, target, cancellationToken);
    }
    /// <summary>报告进度。</summary>
    Task ReportProgressAsync(Guid operationId, double progress, string stage, CancellationToken cancellationToken);
}

/// <summary>内容寻址存储端口。</summary>
public interface IContentStore
{
    /// <summary>写入或复用内容对象。</summary>
    Task<ContentHash> PutAsync(Stream content, CancellationToken cancellationToken);
    /// <summary>打开内容读取流。</summary>
    Task<Stream> OpenReadAsync(ContentHash hash, CancellationToken cancellationToken);
}

/// <summary>操作日志端口。</summary>
public interface IActivityLogger
{
    /// <summary>追加操作日志。</summary>
    Task AppendAsync(ActivityLogEntry entry, CancellationToken cancellationToken);
}

/// <summary>安全审计端口。</summary>
public interface IAuditLogger
{
    /// <summary>追加安全审计。</summary>
    Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken);
}

/// <summary>操作与安全审计日志只读查询端口。</summary>
public interface ILogReader
{
    /// <summary>按时间倒序读取操作日志。</summary>
    Task<IReadOnlyList<ActivityLogEntry>> ListActivitiesAsync(int limit, CancellationToken cancellationToken);
    /// <summary>按时间倒序读取安全审计日志。</summary>
    Task<IReadOnlyList<AuditLogEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>经过 SSRF 防护的出站 HTTP 端口。</summary>
public interface IOutboundHttpClient
{
    /// <summary>发送受策略约束的请求。</summary>
    Task<OutboundHttpResponse> SendAsync(OutboundHttpRequest request, CancellationToken cancellationToken);
}

/// <summary>出站请求。</summary>
/// <param name="Uri">目标 URI。</param>
/// <param name="Method">方法。</param>
/// <param name="Body">可选请求体。</param>
/// <param name="ContentType">请求体媒体类型。</param>
/// <param name="MaximumResponseBytes">本次调用的更小响应上限。</param>
public sealed record OutboundHttpRequest(
    Uri Uri,
    string Method,
    ReadOnlyMemory<byte>? Body = null,
    string? ContentType = null,
    long? MaximumResponseBytes = null);

/// <summary>出站响应。</summary>
/// <param name="StatusCode">状态码。</param>
/// <param name="Content">响应流。</param>
/// <param name="FinalUri">最终 URI。</param>
/// <param name="ContentLength">服务端声明的响应长度；未知时为空。</param>
public sealed record OutboundHttpResponse(int StatusCode, Stream Content, Uri FinalUri, long? ContentLength = null);

/// <summary>Server 侧路径预校验端口。</summary>
public interface IPathValidator
{
    /// <summary>校验绝对工作目录候选。</summary>
    Task<PathValidationResult> ValidateWorkingDirectoryAsync(Guid nodeId, string absolutePath, CancellationToken cancellationToken);
}

/// <summary>24 小时幂等存储端口。</summary>
public interface IIdempotencyStore
{
    /// <summary>读取已完成结果。</summary>
    Task<IdempotencyRecord?> FindAsync(string key, CancellationToken cancellationToken);
    /// <summary>保存首次结果。</summary>
    Task StoreAsync(IdempotencyRecord record, CancellationToken cancellationToken);
}

/// <summary>幂等结果记录。</summary>
/// <param name="Key">幂等键。</param>
/// <param name="RequestHash">请求体哈希。</param>
/// <param name="Response">序列化响应。</param>
/// <param name="ExpiresAt">过期时间。</param>
public sealed record IdempotencyRecord(string Key, string RequestHash, ReadOnlyMemory<byte> Response, DateTimeOffset ExpiresAt);

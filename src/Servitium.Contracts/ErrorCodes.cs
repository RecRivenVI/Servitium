using System.Collections.Generic;

namespace Servitium.Contracts;

/// <summary>
/// 定义协议中稳定、不可改名的错误码。
/// </summary>
public static class ErrorCodes
{
    /// <summary>未认证或会话失效。</summary>
    public const string Unauthenticated = "unauthenticated";
    /// <summary>缺少或无效的 CSRF 令牌。</summary>
    public const string CsrfTokenMissing = "csrf-token-missing";
    /// <summary>已认证但无权限。</summary>
    public const string Forbidden = "forbidden";
    /// <summary>账号已禁用。</summary>
    public const string AccountDisabled = "account-disabled";
    /// <summary>API Key 功能已关闭。</summary>
    public const string ApiKeyDisabled = "api-key-disabled";
    /// <summary>资源不存在。</summary>
    public const string NotFound = "not-found";
    /// <summary>请求与当前状态冲突。</summary>
    public const string Conflict = "conflict";
    /// <summary>系统已完成初始化。</summary>
    public const string AlreadyInitialized = "already-initialized";
    /// <summary>请求校验失败。</summary>
    public const string ValidationFailed = "validation-failed";
    /// <summary>请求触发限流。</summary>
    public const string RateLimited = "rate-limited";
    /// <summary>发生未预期的内部错误。</summary>
    public const string InternalError = "internal-error";
    /// <summary>目标节点不在线。</summary>
    public const string NodeOffline = "node-offline";
    /// <summary>节点协议版本不兼容。</summary>
    public const string NodeIncompatible = "node-incompatible";
    /// <summary>Agent 指令超时。</summary>
    public const string AgentTimeout = "agent-timeout";
    /// <summary>Owner 不可执行所请求的变更。</summary>
    public const string OwnerImmutable = "owner-immutable";
    /// <summary>用户名已被占用。</summary>
    public const string UserNameTaken = "user-name-taken";
    /// <summary>密码不满足强度要求。</summary>
    public const string WeakPassword = "weak-password";
    /// <summary>登录需要 TOTP 验证码。</summary>
    public const string TotpRequired = "totp-required";
    /// <summary>TOTP 验证码无效。</summary>
    public const string TotpInvalid = "totp-invalid";
    /// <summary>OIDC state 无效或已消费。</summary>
    public const string OidcStateInvalid = "oidc-state-invalid";
    /// <summary>OIDC nonce 不匹配。</summary>
    public const string OidcNonceMismatch = "oidc-nonce-mismatch";
    /// <summary>外部身份已绑定其他用户。</summary>
    public const string OidcSubjectConflict = "oidc-subject-conflict";
    /// <summary>OIDC 发现文档读取失败。</summary>
    public const string OidcDiscoveryFailed = "oidc-discovery-failed";
    /// <summary>节点注册令牌无效。</summary>
    public const string RegistrationTokenInvalid = "registration-token-invalid";
    /// <summary>节点仍有已登记实例。</summary>
    public const string NodeHasInstances = "node-has-instances";
    /// <summary>Java 运行时不可用或架构不受支持。</summary>
    public const string JavaRuntimeInvalid = "java-runtime-invalid";
    /// <summary>Java 运行时仍被实例使用。</summary>
    public const string JavaRuntimeInUse = "java-runtime-in-use";
    /// <summary>非导入型 Java 运行时不可删除文件。</summary>
    public const string JavaRuntimeNotImported = "java-runtime-not-imported";
    /// <summary>实例未处于停止状态。</summary>
    public const string InstanceNotStopped = "instance-not-stopped";
    /// <summary>外部工作目录不允许删除。</summary>
    public const string InstanceNotOwned = "instance-not-owned";
    /// <summary>实例工作目录路径不合法。</summary>
    public const string InstancePathInvalid = "instance-path-invalid";
    /// <summary>实例工作目录与其他实例冲突。</summary>
    public const string InstancePathConflict = "instance-path-conflict";
    /// <summary>Minecraft EULA 尚未同意。</summary>
    public const string EulaNotAccepted = "eula-not-accepted";
    /// <summary>高级原始命令模式未启用。</summary>
    public const string RawCommandDisabled = "raw-command-disabled";
    /// <summary>高级原始命令不符合安全约束。</summary>
    public const string RawCommandInvalid = "raw-command-invalid";
    /// <summary>路径逃逸出允许范围。</summary>
    public const string PathEscapesRoot = "path-escapes-root";
    /// <summary>文件名不合法。</summary>
    public const string PathNameInvalid = "path-name-invalid";
    /// <summary>路径包含重解析点。</summary>
    public const string PathReparsePoint = "path-reparse-point";
    /// <summary>文件被运行中的实例占用。</summary>
    public const string FileLocked = "file-locked";
    /// <summary>文件超出允许的大小上限。</summary>
    public const string FileTooLarge = "file-too-large";
    /// <summary>目标卷可用空间不足。</summary>
    public const string DiskSpaceInsufficient = "disk-space-insufficient";
    /// <summary>上传会话已过期。</summary>
    public const string UploadSessionExpired = "upload-session-expired";
    /// <summary>上传分片校验和不匹配。</summary>
    public const string UploadPartChecksumMismatch = "upload-part-checksum-mismatch";
    /// <summary>归档未通过安全检查。</summary>
    public const string ArchiveUnsafe = "archive-unsafe";
    /// <summary>归档解压后超出上限。</summary>
    public const string ArchiveTooLarge = "archive-too-large";
    /// <summary>配置文件解析失败。</summary>
    public const string ConfigParseFailed = "config-parse-failed";
    /// <summary>配置文件格式不受支持。</summary>
    public const string ConfigFormatUnsupported = "config-format-unsupported";
    /// <summary>计划任务数量达到上限。</summary>
    public const string ScheduleLimitReached = "schedule-limit-reached";
    /// <summary>计划任务名称已被占用。</summary>
    public const string ScheduleNameTaken = "schedule-name-taken";
    /// <summary>URL 目标不在允许范围内。</summary>
    public const string UrlNotAllowed = "url-not-allowed";
    /// <summary>HTTP 重定向次数超限。</summary>
    public const string RedirectLimitExceeded = "redirect-limit-exceeded";
    /// <summary>HMCL Channel slug 已存在。</summary>
    public const string HmclChannelSlugTaken = "hmcl-channel-slug-taken";
    /// <summary>HMCL Release 未通过发布门禁。</summary>
    public const string HmclReleaseValidationFailed = "hmcl-release-validation-failed";
    /// <summary>HMCL 内容库缺少声明的对象。</summary>
    public const string HmclContentMissing = "hmcl-content-missing";
    /// <summary>HMCL Channel 没有生效的 Release。</summary>
    public const string HmclNoActiveRelease = "hmcl-no-active-release";
    /// <summary>HMCL Channel 访问令牌无效。</summary>
    public const string HmclTokenInvalid = "hmcl-token-invalid";
    /// <summary>非回环监听缺少 TLS 或可信代理。</summary>
    public const string TlsRequired = "tls-required";
    /// <summary>TLS 证书无效。</summary>
    public const string CertificateInvalid = "certificate-invalid";
    /// <summary>长操作已不能取消。</summary>
    public const string OperationNotCancellable = "operation-not-cancellable";
    /// <summary>幂等键对应了不同请求体。</summary>
    public const string IdempotencyConflict = "idempotency-conflict";

    /// <summary>
    /// 获取所有稳定错误码。
    /// </summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            Unauthenticated,
            CsrfTokenMissing,
            Forbidden,
            AccountDisabled,
            ApiKeyDisabled,
            NotFound,
            Conflict,
            AlreadyInitialized,
            ValidationFailed,
            RateLimited,
            InternalError,
            NodeOffline,
            NodeIncompatible,
            AgentTimeout,
            OwnerImmutable,
            UserNameTaken,
            WeakPassword,
            TotpRequired,
            TotpInvalid,
            OidcStateInvalid,
            OidcNonceMismatch,
            OidcSubjectConflict,
            OidcDiscoveryFailed,
            RegistrationTokenInvalid,
            NodeHasInstances,
            JavaRuntimeInvalid,
            JavaRuntimeInUse,
            JavaRuntimeNotImported,
            InstanceNotStopped,
            InstanceNotOwned,
            InstancePathInvalid,
            InstancePathConflict,
            EulaNotAccepted,
            RawCommandDisabled,
            RawCommandInvalid,
            PathEscapesRoot,
            PathNameInvalid,
            PathReparsePoint,
            FileLocked,
            FileTooLarge,
            DiskSpaceInsufficient,
            UploadSessionExpired,
            UploadPartChecksumMismatch,
            ArchiveUnsafe,
            ArchiveTooLarge,
            ConfigParseFailed,
            ConfigFormatUnsupported,
            ScheduleLimitReached,
            ScheduleNameTaken,
            UrlNotAllowed,
            RedirectLimitExceeded,
            HmclChannelSlugTaken,
            HmclReleaseValidationFailed,
            HmclContentMissing,
            HmclNoActiveRelease,
            HmclTokenInvalid,
            TlsRequired,
            CertificateInvalid,
            OperationNotCancellable,
            IdempotencyConflict,
        ],
        System.StringComparer.Ordinal);
}

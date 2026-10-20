using System;
using System.Collections.Generic;

namespace Servitium.Contracts.Identity;

/// <summary>用户摘要。</summary>
/// <param name="Id">用户标识。</param>
/// <param name="UserName">用户名。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Role">角色稳定值。</param>
/// <param name="Disabled">是否禁用。</param>
/// <param name="LastSignedInAt">上次登录时间。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="HasExternalIdentity">是否绑定外部身份。</param>
/// <param name="GrantedInstanceCount">已授权实例数。</param>
/// <param name="TotpEnabled">是否启用 TOTP。</param>
/// <param name="HasWindowsIdentity">是否绑定 Windows 身份。</param>
public sealed record UserDto(
    Guid Id,
    string UserName,
    string DisplayName,
    string Role,
    bool Disabled,
    DateTimeOffset? LastSignedInAt,
    DateTimeOffset CreatedAt,
    bool HasExternalIdentity = false,
    int GrantedInstanceCount = 0,
    bool TotpEnabled = false,
    bool HasWindowsIdentity = false);

/// <summary>认证主体快照。</summary>
/// <param name="User">用户。</param>
/// <param name="GrantedInstanceIds">授权实例。</param>
/// <param name="Language">界面语言。</param>
public sealed record PrincipalDto(UserDto User, IReadOnlyList<Guid> GrantedInstanceIds, string Language);

/// <summary>登录请求。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="Password">密码。</param>
/// <param name="TotpCode">可选 TOTP 验证码。</param>
public sealed record SignInRequest(string UserName, string Password, string? TotpCode = null);

/// <summary>提交待验证会话的 TOTP 或一次性恢复码。</summary>
/// <param name="Code">TOTP 或恢复码。</param>
public sealed record CompleteTotpSignInRequest(string Code);

/// <summary>初始 Owner 创建请求。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Password">密码。</param>
/// <param name="Language">首选语言。</param>
/// <param name="InstallToken">非回环初始化令牌。</param>
public sealed record InitializeRequest(string UserName, string DisplayName, string Password, string Language, string? InstallToken);

/// <summary>认证状态。</summary>
/// <param name="Initialized">是否已初始化。</param>
/// <param name="Authenticated">是否已认证。</param>
/// <param name="Principal">当前主体。</param>
public sealed record AuthenticationStateDto(bool Initialized, bool Authenticated, PrincipalDto? Principal);

/// <summary>双提交 Cookie 使用的 CSRF 令牌。</summary>
public sealed record CsrfTokenDto(string Token);

/// <summary>登录需要继续完成的挑战。</summary>
/// <param name="Code">稳定挑战码，例如 <c>totp-required</c>。</param>
public sealed record AuthenticationChallengeDto(string Code);

/// <summary>创建 API Key 请求。</summary>
/// <param name="Name">显示名称。</param>
/// <param name="Scope">权限范围。</param>
/// <param name="ExpiresAt">可选过期时间。</param>
public sealed record CreateApiKeyRequest(string Name, string Scope, DateTimeOffset? ExpiresAt);

/// <summary>只返回一次的 API Key。</summary>
/// <param name="Id">密钥标识。</param>
/// <param name="Name">显示名称。</param>
/// <param name="PlaintextKey">明文密钥。</param>
/// <param name="ExpiresAt">可选过期时间。</param>
public sealed record CreatedApiKeyDto(Guid Id, string Name, string PlaintextKey, DateTimeOffset? ExpiresAt);

/// <summary>创建用户请求。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Password">初始密码。</param>
/// <param name="Role">角色。</param>
public sealed record CreateUserRequest(string UserName, string DisplayName, string Password, string Role);

/// <summary>匿名登录页启动信息。</summary>
/// <param name="Initialized">是否已初始化。</param>
/// <param name="OidcEnabled">是否启用 OIDC。</param>
/// <param name="OidcDisplayName">身份提供方显示名。</param>
/// <param name="OidcIconUrl">身份提供方图标地址。</param>
/// <param name="AutoRedirectToOidc">是否自动跳转。</param>
/// <param name="LoginPageText">登录页说明。</param>
public sealed record AuthenticationBootstrapDto(
    bool Initialized,
    bool OidcEnabled,
    string? OidcDisplayName,
    Uri? OidcIconUrl,
    bool AutoRedirectToOidc,
    string? LoginPageText);

/// <summary>修改本人资料。</summary>
/// <param name="DisplayName">显示名。</param>
/// <param name="Language">首选语言。</param>
public sealed record UpdateProfileRequest(string DisplayName, string Language);

/// <summary>修改本人密码。</summary>
/// <param name="CurrentPassword">当前密码。</param>
/// <param name="NewPassword">新密码。</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>会话摘要。</summary>
/// <param name="Id">会话标识。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="LastSeenAt">最后活动时间。</param>
/// <param name="ExpiresAt">滑动过期时间。</param>
/// <param name="Current">是否当前会话。</param>
public sealed record SessionDto(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, bool Current);

/// <summary>API Key 摘要，不包含明文或哈希。</summary>
/// <param name="Id">密钥标识。</param>
/// <param name="Name">名称。</param>
/// <param name="Scope">权限范围。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="ExpiresAt">过期时间。</param>
/// <param name="LastUsedAt">最后使用时间。</param>
/// <param name="Revoked">是否吊销。</param>
public sealed record ApiKeyDto(Guid Id, string Name, string Scope, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, bool Revoked);

/// <summary>开始绑定 TOTP 的材料。</summary>
/// <param name="Secret">Base32 密钥，仅在此步骤返回。</param>
/// <param name="OtpAuthUri">标准 otpauth URI。</param>
public sealed record TotpSetupDto(string Secret, Uri OtpAuthUri);

/// <summary>确认 TOTP 绑定。</summary>
/// <param name="Code">六位验证码。</param>
public sealed record ConfirmTotpRequest(string Code);

/// <summary>TOTP 恢复码集合，仅返回一次。</summary>
/// <param name="RecoveryCodes">恢复码。</param>
public sealed record TotpRecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);

/// <summary>管理员修改用户。</summary>
/// <param name="DisplayName">显示名。</param>
/// <param name="Role">角色；Owner 不可由此端点设置。</param>
public sealed record UpdateUserRequest(string DisplayName, string Role);

/// <summary>管理员重置密码。</summary>
/// <param name="Password">新密码。</param>
public sealed record ResetPasswordRequest(string Password);

/// <summary>全量实例授权请求。</summary>
/// <param name="InstanceIds">授权实例标识。</param>
public sealed record ReplaceInstanceGrantsRequest(IReadOnlyList<Guid> InstanceIds);

/// <summary>增量实例授权请求。</summary>
/// <param name="InstanceIds">新增实例标识。</param>
public sealed record AddInstanceGrantsRequest(IReadOnlyList<Guid> InstanceIds);

/// <summary>Windows 身份绑定。</summary>
/// <param name="Sid">规范化 Windows SID。</param>
public sealed record WindowsIdentityBindingDto(string Sid);

/// <summary>用户可访问的实例授权集合。</summary>
public sealed record InstanceGrantsDto(IReadOnlyList<Guid> InstanceIds);

/// <summary>本机管道一次性令牌换取请求。</summary>
/// <param name="Token">60 秒一次性令牌。</param>
/// <param name="CodeVerifier">Desktop OIDC 换票的 PKCE verifier；本机管道换票不提供。</param>
public sealed record LocalSessionExchangeRequest(string Token, string? CodeVerifier = null);

/// <summary>外部身份绑定请求。</summary>
/// <param name="AuthorizationCode">OIDC 授权码；完成绑定时提供。</param>
/// <param name="State">防重放 state；完成绑定时提供。</param>
/// <param name="DesktopCallback">Desktop 发起绑定时使用的随机回环完成地址。</param>
public sealed record BindExternalIdentityRequest(string? AuthorizationCode = null, string? State = null, Uri? DesktopCallback = null);

/// <summary>Desktop 外部身份绑定的系统浏览器入口。</summary>
public sealed record OidcBindingStartDto(Uri AuthorizationUri);

/// <summary>用户统计。</summary>
/// <param name="Total">用户总数。</param>
/// <param name="Enabled">启用用户数。</param>
/// <param name="Disabled">禁用用户数。</param>
/// <param name="WithTotp">已绑定 TOTP 用户数。</param>
/// <param name="WithExternalIdentity">已绑定外部身份用户数。</param>
/// <param name="RoleCounts">按稳定角色名称统计的用户数。</param>
/// <param name="ActiveSessions">当前有效的非受限会话数。</param>
public sealed record UserOverviewDto(
    int Total,
    int Enabled,
    int Disabled,
    int WithTotp,
    int WithExternalIdentity,
    IReadOnlyDictionary<string, int> RoleCounts,
    int ActiveSessions);

/// <summary>用户批处理请求。</summary>
/// <param name="UserIds">不重复的目标用户标识。</param>
public sealed record BatchUsersRequest(IReadOnlyList<Guid> UserIds);

/// <summary>用户批处理中单个目标的结果。</summary>
/// <param name="UserId">目标用户。</param>
/// <param name="Succeeded">是否成功。</param>
/// <param name="ErrorCode">失败时的稳定错误码。</param>
public sealed record BatchUserResultItemDto(Guid UserId, bool Succeeded, string? ErrorCode);

/// <summary>用户批处理结果。</summary>
/// <param name="Items">逐目标结果。</param>
public sealed record BatchUsersResultDto(IReadOnlyList<BatchUserResultItemDto> Items);

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Transport;
using Servitium.Contracts.Identity;

namespace Servitium.Client;

public sealed partial class ServitiumApiClient
{
    /// <summary>读取初始化页的身份提供方与安装状态。</summary>
    public Task<AuthenticationBootstrapDto> GetAuthenticationBootstrapAsync(CancellationToken cancellationToken)
    {
        return GetJsonAsync<AuthenticationBootstrapDto>("api/v1/auth/bootstrap", cancellationToken);
    }

    /// <summary>经 bootstrap 兼容入口创建首个 Owner。</summary>
    public Task InitializeFromBootstrapAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        return SendJsonNoContentAsync(HttpMethod.Post, "api/v1/auth/bootstrap", request, cancellationToken);
    }

    /// <summary>显式取得 CSRF 令牌并同步客户端 Cookie。</summary>
    public Task<CsrfTokenDto> GetCsrfTokenAsync(CancellationToken cancellationToken)
    {
        return GetJsonAsync<CsrfTokenDto>("api/v1/auth/csrf", cancellationToken);
    }

    /// <summary>修改个人资料。</summary>
    public Task<UserDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpdateProfileRequest, UserDto>(HttpMethod.Patch, "api/v1/auth/me", request, cancellationToken);
    }

    /// <summary>修改密码；成功后当前会话失效。</summary>
    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        return SendJsonNoContentAsync(HttpMethod.Post, "api/v1/auth/me/password", request, cancellationToken);
    }

    /// <summary>列出个人会话。</summary>
    public Task<CursorPage<SessionDto>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        return ListSessionsAsync(null, 50, cancellationToken);
    }

    /// <summary>分页列出个人会话。</summary>
    public Task<CursorPage<SessionDto>> ListSessionsAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<SessionDto>($"api/v1/auth/me/sessions?cursor={Escape(cursor)}&limit={limit}", cancellationToken);
    }

    /// <summary>撤销个人会话。</summary>
    public Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/auth/me/sessions/{sessionId:D}", cancellationToken);
    }

    /// <summary>开始配置 TOTP。</summary>
    public Task<TotpSetupDto> BeginTotpAsync(CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<TotpSetupDto>(HttpMethod.Post, "api/v1/auth/me/totp", cancellationToken);
    }

    /// <summary>确认 TOTP 并返回一次性恢复码。</summary>
    public Task<TotpRecoveryCodesDto> ConfirmTotpAsync(ConfirmTotpRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<ConfirmTotpRequest, TotpRecoveryCodesDto>(HttpMethod.Post, "api/v1/auth/me/totp/confirm", request, cancellationToken);
    }

    /// <summary>移除自己的 TOTP。</summary>
    public Task RemoveTotpAsync(CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, "api/v1/auth/me/totp", cancellationToken);
    }

    /// <summary>列出个人 API Key。</summary>
    public Task<CursorPage<ApiKeyDto>> ListApiKeysAsync(CancellationToken cancellationToken)
    {
        return ListApiKeysAsync(null, 50, cancellationToken);
    }

    /// <summary>分页列出个人 API Key。</summary>
    public Task<CursorPage<ApiKeyDto>> ListApiKeysAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<ApiKeyDto>($"api/v1/auth/me/api-keys?cursor={Escape(cursor)}&limit={limit}", cancellationToken);
    }

    /// <summary>创建 API Key；明文仅在本次响应出现。</summary>
    public Task<CreatedApiKeyDto> CreateApiKeyAsync(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateApiKeyRequest, CreatedApiKeyDto>(HttpMethod.Post, "api/v1/auth/me/api-keys", request, cancellationToken);
    }

    /// <summary>经认证根入口创建 API Key。</summary>
    public Task<CreatedApiKeyDto> CreateScopedApiKeyAsync(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateApiKeyRequest, CreatedApiKeyDto>(HttpMethod.Post, "api/v1/auth/api-keys", request, cancellationToken);
    }

    /// <summary>撤销个人 API Key。</summary>
    public Task RevokeApiKeyAsync(Guid apiKeyId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/auth/me/api-keys/{apiKeyId:D}", cancellationToken);
    }

    /// <summary>移除自己的 OIDC 绑定。</summary>
    public Task RemoveOwnExternalIdentityAsync(CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, "api/v1/auth/me/external-identity", cancellationToken);
    }

    /// <summary>构造只能交给系统浏览器打开的 OIDC 授权入口。</summary>
    public Uri GetOidcAuthorizationUri(
        Uri? returnUrl,
        bool bind,
        Uri? desktopCallback = null,
        string? desktopCodeChallenge = null)
    {
        string path = $"api/v1/auth/oidc/authorize?returnUrl={Escape(returnUrl?.AbsoluteUri)}&bind={Bool(bind)}" +
            $"&desktopCallback={Escape(desktopCallback?.AbsoluteUri)}&desktopCodeChallenge={Escape(desktopCodeChallenge)}";
        return new Uri(BaseAddress, path);
    }

    /// <summary>构造 OIDC 回调地址；浏览器负责访问并接收重定向。</summary>
    public Uri GetOidcCallbackUri(string code, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        return new Uri(BaseAddress, $"api/v1/auth/oidc/callback?code={Escape(code)}&state={Escape(state)}");
    }

    /// <summary>完成已登录用户的 OIDC 身份绑定。</summary>
    public Task BindOidcAsync(BindExternalIdentityRequest request, CancellationToken cancellationToken)
    {
        return SendJsonNoContentAsync(HttpMethod.Post, "api/v1/auth/oidc/bind", request, cancellationToken);
    }

    /// <summary>以已认证 Desktop 会话创建一次性 OIDC 绑定流程。</summary>
    public Task<OidcBindingStartDto> BeginOidcBindingAsync(Uri desktopCallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desktopCallback);
        return SendJsonAsync<BindExternalIdentityRequest, OidcBindingStartDto>(
            HttpMethod.Post,
            "api/v1/auth/oidc/bind",
            new BindExternalIdentityRequest(DesktopCallback: desktopCallback),
            cancellationToken);
    }

    /// <summary>列出用户。</summary>
    public Task<CursorPage<UserDto>> ListUsersAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        return ListUsersAsync(null, null, cursor, limit, cancellationToken);
    }

    /// <summary>按角色、用户名或显示名筛选用户。</summary>
    public Task<CursorPage<UserDto>> ListUsersAsync(string? role, string? name, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<UserDto>($"api/v1/users?role={Escape(role)}&name={Escape(name)}&cursor={Escape(cursor)}&limit={limit}", cancellationToken);
    }

    /// <summary>读取用户治理概览。</summary>
    public Task<UserOverviewDto> GetUserOverviewAsync(CancellationToken cancellationToken)
    {
        return GetJsonAsync<UserOverviewDto>("api/v1/users/overview", cancellationToken);
    }

    /// <summary>创建用户。</summary>
    public Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<CreateUserRequest, UserDto>(HttpMethod.Post, "api/v1/users", request, cancellationToken);
    }

    /// <summary>读取用户。</summary>
    public Task<UserDto> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<UserDto>($"api/v1/users/{userId:D}", cancellationToken);
    }

    /// <summary>修改用户。</summary>
    public Task<UserDto> UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpdateUserRequest, UserDto>(HttpMethod.Patch, $"api/v1/users/{userId:D}", request, cancellationToken);
    }

    /// <summary>重置用户密码。</summary>
    public Task ResetUserPasswordAsync(Guid userId, ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        return SendJsonNoContentAsync(HttpMethod.Post, $"api/v1/users/{userId:D}/password", request, cancellationToken);
    }

    /// <summary>禁用用户。</summary>
    public Task<UserDto> DisableUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<UserDto>(HttpMethod.Post, $"api/v1/users/{userId:D}/disable", cancellationToken);
    }

    /// <summary>启用用户。</summary>
    public Task<UserDto> EnableUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<UserDto>(HttpMethod.Post, $"api/v1/users/{userId:D}/enable", cancellationToken);
    }

    /// <summary>删除用户。</summary>
    public Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/users/{userId:D}", cancellationToken);
    }

    /// <summary>批量禁用用户。</summary>
    public Task<BatchUsersResultDto> DisableUsersAsync(BatchUsersRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<BatchUsersRequest, BatchUsersResultDto>(HttpMethod.Post, "api/v1/users/batch/disable", request, cancellationToken);
    }

    /// <summary>批量删除用户。</summary>
    public Task<BatchUsersResultDto> DeleteUsersAsync(BatchUsersRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<BatchUsersRequest, BatchUsersResultDto>(HttpMethod.Post, "api/v1/users/batch/delete", request, cancellationToken);
    }

    /// <summary>重置用户 TOTP。</summary>
    public Task ResetUserTotpAsync(Guid userId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/users/{userId:D}/totp", cancellationToken);
    }

    /// <summary>撤销用户全部会话。</summary>
    public Task RevokeUserSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/users/{userId:D}/sessions", cancellationToken);
    }

    /// <summary>移除用户外部身份。</summary>
    public Task RemoveUserExternalIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/users/{userId:D}/external-identity", cancellationToken);
    }

    /// <summary>读取用户实例授权。</summary>
    public Task<InstanceGrantsDto> GetUserGrantsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<InstanceGrantsDto>($"api/v1/users/{userId:D}/grants", cancellationToken);
    }

    /// <summary>替换用户实例授权。</summary>
    public Task<InstanceGrantsDto> ReplaceUserGrantsAsync(Guid userId, ReplaceInstanceGrantsRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<ReplaceInstanceGrantsRequest, InstanceGrantsDto>(HttpMethod.Put, $"api/v1/users/{userId:D}/grants", request, cancellationToken);
    }

    /// <summary>追加用户实例授权。</summary>
    public Task<InstanceGrantsDto> AddUserGrantsAsync(Guid userId, AddInstanceGrantsRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<AddInstanceGrantsRequest, InstanceGrantsDto>(HttpMethod.Post, $"api/v1/users/{userId:D}/grants", request, cancellationToken);
    }

    /// <summary>移除单项用户实例授权。</summary>
    public Task RemoveUserGrantAsync(Guid userId, Guid instanceId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, $"api/v1/users/{userId:D}/grants/{instanceId:D}", cancellationToken);
    }

    /// <summary>读取 Windows 身份绑定。</summary>
    public Task<WindowsIdentityBindingDto> GetWindowsIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<WindowsIdentityBindingDto>($"api/v1/users/{userId:D}/windows-identity", cancellationToken);
    }

    /// <summary>设置 Windows 身份绑定。</summary>
    public Task<WindowsIdentityBindingDto> SetWindowsIdentityAsync(Guid userId, WindowsIdentityBindingDto request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<WindowsIdentityBindingDto, WindowsIdentityBindingDto>(HttpMethod.Put, $"api/v1/users/{userId:D}/windows-identity", request, cancellationToken);
    }

    private static string Escape(string? value)
    {
        return value is null ? string.Empty : Uri.EscapeDataString(value);
    }
}

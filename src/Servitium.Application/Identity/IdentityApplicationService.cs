using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Common;
using Servitium.Contracts;
using Servitium.Domain.Identity;

namespace Servitium.Application.Identity;

/// <summary>身份应用服务的选项。</summary>
public sealed class IdentityServiceOptions
{
    private long _sessionLifetimeTicks;
    private long _absoluteSessionLifetimeTicks;
    private int _apiKeysEnabled;

    /// <summary>初始化身份运行策略。</summary>
    public IdentityServiceOptions(TimeSpan sessionLifetime, TimeSpan absoluteSessionLifetime, bool apiKeysEnabled)
    {
        Update(sessionLifetime, absoluteSessionLifetime, apiKeysEnabled);
    }

    /// <summary>滑动会话生存期。</summary>
    public TimeSpan SessionLifetime => TimeSpan.FromTicks(Volatile.Read(ref _sessionLifetimeTicks));

    /// <summary>会话绝对生存期。</summary>
    public TimeSpan AbsoluteSessionLifetime => TimeSpan.FromTicks(Volatile.Read(ref _absoluteSessionLifetimeTicks));

    /// <summary>API Key 总开关。</summary>
    public bool ApiKeysEnabled => Volatile.Read(ref _apiKeysEnabled) != 0;

    /// <summary>原子替换当前运行策略。</summary>
    public void Update(TimeSpan sessionLifetime, TimeSpan absoluteSessionLifetime, bool apiKeysEnabled)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sessionLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(absoluteSessionLifetime, sessionLifetime);
        Volatile.Write(ref _sessionLifetimeTicks, sessionLifetime.Ticks);
        Volatile.Write(ref _absoluteSessionLifetimeTicks, absoluteSessionLifetime.Ticks);
        Volatile.Write(ref _apiKeysEnabled, apiKeysEnabled ? 1 : 0);
    }
}

/// <summary>成功认证后返回的会话材料。</summary>
public sealed record AuthenticationResult(
    string SessionToken,
    StoredSession Session,
    StoredUser User,
    IReadOnlySet<Guid> GrantedInstanceIds);

/// <summary>一次性返回的 API Key 材料。</summary>
public sealed record ApiKeyCreationResult(string PlaintextKey, StoredApiKey ApiKey);

/// <summary>集中实现身份初始化、认证、会话和用户管理用例。</summary>
public sealed class IdentityApplicationService(
    IIdentityStore store,
    IPasswordHasher passwordHasher,
    ITotpService totpService,
    ISecretProtector secretProtector,
    ITokenGenerator tokenGenerator,
    IClock clock,
    IGuidFactory guidFactory,
    IAuthorizationService authorizationService,
    IdentityServiceOptions options)
{
    private static readonly HashSet<string> WeakPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234",
        "administrator",
        "minecraft123",
        "servitium123",
        "123456789012",
        "qwertyuiop12",
    };

    /// <summary>判断系统是否完成初始化。</summary>
    public async Task<bool> IsInitializedAsync(CancellationToken cancellationToken)
    {
        return await store.HasUsersAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>原子创建初始 Owner。</summary>
    public async Task<ApplicationResult<StoredUser>> InitializeAsync(
        string userName,
        string displayName,
        string password,
        string language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(language);
        string? passwordError = ValidatePassword(password);
        if (passwordError is not null)
        {
            return ApplicationResult.Failure<StoredUser>(passwordError);
        }

        string normalizedName = NormalizeUserName(userName);
        DateTimeOffset now = clock.UtcNow;
        StoredUser owner = new(
            guidFactory.Create(),
            normalizedName,
            displayName.Trim(),
            Role.Owner,
            passwordHasher.Hash(password),
            NormalizeLanguage(language),
            now,
            null,
            false,
            null,
            false,
            0);

        bool created = await store.TryCreateInitialOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
        return created
            ? ApplicationResult.Success(owner)
            : ApplicationResult.Failure<StoredUser>(ErrorCodes.AlreadyInitialized);
    }

    /// <summary>验证密码；绑定 TOTP 时只创建五分钟的待验证会话。</summary>
    public async Task<ApplicationResult<AuthenticationResult>> SignInAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(password);
        StoredUser? user = await store.FindUserByNameAsync(NormalizeUserName(userName), cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted || !passwordHasher.Verify(password, user.PasswordHash))
        {
            return ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.Unauthenticated);
        }

        if (user.Role == Role.Disabled)
        {
            return ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.AccountDisabled);
        }

        if (passwordHasher.NeedsRehash(user.PasswordHash))
        {
            user = user with { PasswordHash = passwordHasher.Hash(password) };
            await store.UpdateUserAsync(user, cancellationToken).ConfigureAwait(false);
        }

        if (user.TotpConfirmed)
        {
            return ApplicationResult.Success(await CreateSessionAsync(user, true, cancellationToken).ConfigureAwait(false));
        }

        StoredUser signedInUser = user with { LastSignedInAt = clock.UtcNow };
        await store.UpdateUserAsync(signedInUser, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(await CreateSessionAsync(signedInUser, false, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>完成待验证会话的 TOTP 或恢复码校验并轮换为正式会话。</summary>
    public async Task<ApplicationResult<AuthenticationResult>> CompleteTotpSignInAsync(
        AuthenticationResult pending,
        string code,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(code);
        if (!pending.Session.IsRestricted || !pending.User.TotpConfirmed)
        {
            return ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.Unauthenticated);
        }

        bool verified = VerifyTotp(pending.User, code) ||
            await store.ConsumeRecoveryCodeAsync(pending.User.Id, HashToken(code.Trim().ToUpperInvariant()), cancellationToken).ConfigureAwait(false);
        if (!verified)
        {
            return ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.TotpInvalid);
        }

        await store.RevokeSessionAsync(pending.Session.Id, cancellationToken).ConfigureAwait(false);
        StoredUser signedIn = pending.User with { LastSignedInAt = clock.UtcNow };
        await store.UpdateUserAsync(signedIn, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(await CreateSessionAsync(signedIn, false, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>验证并按策略滑动持久会话。</summary>
    public async Task<AuthenticationResult?> AuthenticateSessionAsync(string token, CancellationToken cancellationToken)
    {
        StoredSession? session = await store.FindSessionAsync(HashToken(token), cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        DateTimeOffset now = clock.UtcNow;
        if (session.ExpiresAt <= now || session.AbsoluteExpiresAt <= now)
        {
            await store.RevokeSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
            return null;
        }

        StoredUser? user = await store.FindUserAsync(session.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted || user.Role == Role.Disabled || user.SecurityStamp != session.SecurityStamp)
        {
            await store.RevokeSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
            return null;
        }

        DateTimeOffset slidingExpiry = Min(now.Add(options.SessionLifetime), session.AbsoluteExpiresAt);
        if (!session.IsRestricted && now - session.LastSeenAt >= TimeSpan.FromMinutes(1))
        {
            await store.TouchSessionAsync(session.Id, now, slidingExpiry, cancellationToken).ConfigureAwait(false);
            session = session with { LastSeenAt = now, ExpiresAt = slidingExpiry };
        }

        IReadOnlySet<Guid> grants = await store.ListInstanceGrantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new AuthenticationResult(token, session, user, grants);
    }

    /// <summary>创建用户并强制单一授权入口。</summary>
    public async Task<ApplicationResult<StoredUser>> CreateUserAsync(
        AuthorizationPrincipal actor,
        string userName,
        string displayName,
        string password,
        Role role,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(password);
        AuthorizationDecision decision = authorizationService.Authorize(actor, new(ResourceKind.User), ServitiumAction.ManageUsers);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<StoredUser>(decision.Code!);
        }

        if (role == Role.Owner)
        {
            return ApplicationResult.Failure<StoredUser>(ErrorCodes.OwnerImmutable);
        }

        string? passwordError = ValidatePassword(password);
        if (passwordError is not null)
        {
            return ApplicationResult.Failure<StoredUser>(passwordError);
        }

        StoredUser user = new(
            guidFactory.Create(),
            NormalizeUserName(userName),
            displayName.Trim(),
            role,
            passwordHasher.Hash(password),
            "zh-Hans",
            clock.UtcNow,
            null,
            false,
            null,
            false,
            0);
        bool created = await store.TryCreateUserAsync(user, cancellationToken).ConfigureAwait(false);
        return created
            ? ApplicationResult.Success(user)
            : ApplicationResult.Failure<StoredUser>(ErrorCodes.UserNameTaken);
    }

    /// <summary>创建仅显示一次的 API Key。</summary>
    public async Task<ApplicationResult<ApiKeyCreationResult>> CreateApiKeyAsync(
        AuthorizationPrincipal actor,
        string name,
        string scope,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(scope);
        if (!options.ApiKeysEnabled)
        {
            return ApplicationResult.Failure<ApiKeyCreationResult>(ErrorCodes.ApiKeyDisabled);
        }

        string plaintext = tokenGenerator.Generate(32);
        StoredApiKey key = new(
            guidFactory.Create(),
            actor.UserId,
            name.Trim(),
            HashToken(plaintext),
            scope.Trim(),
            clock.UtcNow,
            expiresAt?.ToUniversalTime(),
            null,
            false);
        await store.AddApiKeyAsync(key, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(new ApiKeyCreationResult(plaintext, key));
    }

    /// <summary>验证 API Key 并返回授权主体。</summary>
    public async Task<AuthenticationResult?> AuthenticateApiKeyAsync(string plaintext, CancellationToken cancellationToken)
    {
        if (!options.ApiKeysEnabled)
        {
            return null;
        }

        StoredApiKey? key = await store.FindApiKeyAsync(HashToken(plaintext), cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = clock.UtcNow;
        if (key is null || key.IsRevoked || key.ExpiresAt <= now)
        {
            return null;
        }

        StoredUser? user = await store.FindUserAsync(key.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted || user.Role == Role.Disabled)
        {
            return null;
        }

        await store.TouchApiKeyAsync(key.Id, now, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<Guid> grants = await store.ListInstanceGrantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        StoredSession syntheticSession = new(key.Id, user.Id, key.KeyHash, key.CreatedAt, now, key.ExpiresAt ?? DateTimeOffset.MaxValue, key.ExpiresAt ?? DateTimeOffset.MaxValue, user.SecurityStamp, false);
        return new AuthenticationResult(plaintext, syntheticSession, user, grants);
    }

    /// <summary>修改当前用户资料。</summary>
    public async Task<ApplicationResult<StoredUser>> UpdateProfileAsync(
        Guid userId,
        string displayName,
        string language,
        CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            return ApplicationResult.Failure<StoredUser>(ErrorCodes.NotFound);
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 128 || displayName.Any(char.IsControl))
        {
            return ApplicationResult.Failure<StoredUser>(ErrorCodes.ValidationFailed);
        }

        StoredUser updated = user with { DisplayName = displayName.Trim(), PreferredLanguage = NormalizeLanguage(language) };
        await store.UpdateUserAsync(updated, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(updated);
    }

    /// <summary>验证当前密码后修改密码，并撤销当前会话之外的全部现有会话。</summary>
    public async Task<ApplicationResult<bool>> ChangePasswordAsync(
        Guid userId,
        Guid retainedSessionId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || !passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return ApplicationResult.Failure<bool>(ErrorCodes.Unauthenticated);
        }

        string? passwordError = ValidatePassword(newPassword);
        if (passwordError is not null)
        {
            return ApplicationResult.Failure<bool>(passwordError);
        }

        await store.UpdateUserAsync(user with { PasswordHash = passwordHasher.Hash(newPassword), SecurityStamp = user.SecurityStamp + 1 }, cancellationToken).ConfigureAwait(false);
        await store.RevokeOtherSessionsAsync(userId, retainedSessionId, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(true);
    }

    /// <summary>开始 TOTP 绑定并暂存受 DPAPI 保护的密钥。</summary>
    public async Task<ApplicationResult<string>> BeginTotpSetupAsync(Guid userId, CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            return ApplicationResult.Failure<string>(ErrorCodes.NotFound);
        }

        string secret = totpService.GenerateSecret();
        byte[] plaintext = Encoding.ASCII.GetBytes(secret);
        try
        {
            byte[] protectedSecret = secretProtector.Protect(plaintext);
            await store.UpdateUserAsync(user with
            {
                ProtectedTotpSecret = new ReadOnlyMemory<byte>(protectedSecret),
                TotpConfirmed = false,
            }, cancellationToken).ConfigureAwait(false);
            await store.ReplaceRecoveryCodesAsync(userId, [], cancellationToken).ConfigureAwait(false);
            return ApplicationResult.Success(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>确认 TOTP 并返回只显示一次的恢复码。</summary>
    public async Task<ApplicationResult<IReadOnlyList<string>>> ConfirmTotpAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user?.ProtectedTotpSecret is null || user.TotpConfirmed)
        {
            return ApplicationResult.Failure<IReadOnlyList<string>>(ErrorCodes.Conflict);
        }

        if (!VerifyTotp(user, code))
        {
            return ApplicationResult.Failure<IReadOnlyList<string>>(ErrorCodes.TotpInvalid);
        }

        string[] plaintextCodes = [.. Enumerable.Range(0, 10).Select(_ => GenerateRecoveryCode())];
        StoredRecoveryCode[] stored = [.. plaintextCodes.Select(value => new StoredRecoveryCode(guidFactory.Create(), userId, HashToken(value), clock.UtcNow))];
        await store.ReplaceRecoveryCodesAsync(userId, stored, cancellationToken).ConfigureAwait(false);
        await store.UpdateUserAsync(user with { TotpConfirmed = true, SecurityStamp = user.SecurityStamp + 1 }, cancellationToken).ConfigureAwait(false);
        await store.RevokeAllSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success<IReadOnlyList<string>>(plaintextCodes);
    }

    /// <summary>解绑当前用户的 TOTP，并撤销其会话。</summary>
    public async Task<ApplicationResult<bool>> RemoveTotpAsync(Guid userId, CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return ApplicationResult.Failure<bool>(ErrorCodes.NotFound);
        }

        await store.ReplaceRecoveryCodesAsync(userId, [], cancellationToken).ConfigureAwait(false);
        await store.UpdateUserAsync(user with { ProtectedTotpSecret = null, TotpConfirmed = false, SecurityStamp = user.SecurityStamp + 1 }, cancellationToken).ConfigureAwait(false);
        await store.RevokeAllSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(true);
    }

    /// <summary>列出当前用户会话。</summary>
    public Task<IReadOnlyList<StoredSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return store.ListSessionsAsync(userId, cancellationToken);
    }

    /// <summary>列出当前用户 API Key。</summary>
    public Task<IReadOnlyList<StoredApiKey>> ListApiKeysAsync(Guid userId, CancellationToken cancellationToken)
    {
        return store.ListApiKeysAsync(userId, cancellationToken);
    }

    /// <summary>管理员修改用户资料与角色。</summary>
    public async Task<ApplicationResult<StoredUser>> UpdateUserAsync(
        AuthorizationPrincipal actor,
        Guid userId,
        string displayName,
        Role role,
        CancellationToken cancellationToken)
    {
        AuthorizationDecision decision = authorizationService.Authorize(actor, new(ResourceKind.User, userId), ServitiumAction.ManageUsers);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<StoredUser>(decision.Code!);
        }

        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            return ApplicationResult.Failure<StoredUser>(ErrorCodes.NotFound);
        }

        if (user.Role == Role.Owner || role is Role.Owner or Role.Disabled || string.IsNullOrWhiteSpace(displayName) || displayName.Any(char.IsControl))
        {
            return ApplicationResult.Failure<StoredUser>(user.Role == Role.Owner || role == Role.Owner ? ErrorCodes.OwnerImmutable : ErrorCodes.ValidationFailed);
        }

        StoredUser updated = user with { DisplayName = displayName.Trim(), Role = role, SecurityStamp = user.SecurityStamp + 1 };
        await store.UpdateUserAsync(updated, cancellationToken).ConfigureAwait(false);
        await store.RevokeAllSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(updated);
    }

    /// <summary>管理员重置用户密码。</summary>
    public async Task<ApplicationResult<bool>> ResetPasswordAsync(
        AuthorizationPrincipal actor,
        Guid userId,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(password);
        AuthorizationDecision decision = authorizationService.Authorize(actor, new(ResourceKind.User, userId), ServitiumAction.ManageUsers);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<bool>(decision.Code!);
        }

        string? passwordError = ValidatePassword(password);
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            return ApplicationResult.Failure<bool>(ErrorCodes.NotFound);
        }

        if (user.Role == Role.Owner && actor.UserId != userId)
        {
            return ApplicationResult.Failure<bool>(ErrorCodes.OwnerImmutable);
        }

        if (passwordError is not null)
        {
            return ApplicationResult.Failure<bool>(passwordError);
        }

        await store.UpdateUserAsync(user with { PasswordHash = passwordHasher.Hash(password), SecurityStamp = user.SecurityStamp + 1 }, cancellationToken).ConfigureAwait(false);
        await store.RevokeAllSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(true);
    }

    /// <summary>管理员禁用或启用用户。</summary>
    public async Task<ApplicationResult<StoredUser>> SetUserDisabledAsync(
        AuthorizationPrincipal actor,
        Guid userId,
        bool disabled,
        CancellationToken cancellationToken)
    {
        AuthorizationDecision decision = authorizationService.Authorize(actor, new(ResourceKind.User, userId), ServitiumAction.ManageUsers);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<StoredUser>(decision.Code!);
        }

        StoredUser? updated = await store.SetUserDisabledAsync(userId, disabled, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            StoredUser? target = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
            return ApplicationResult.Failure<StoredUser>(target?.Role == Role.Owner ? ErrorCodes.OwnerImmutable : ErrorCodes.NotFound);
        }

        await store.RevokeAllSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(updated);
    }

    /// <summary>管理员软删除用户并级联撤销认证材料。</summary>
    public async Task<ApplicationResult<bool>> DeleteUserAsync(AuthorizationPrincipal actor, Guid userId, CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            return ApplicationResult.Failure<bool>(ErrorCodes.NotFound);
        }

        if (user.Role == Role.Owner)
        {
            return ApplicationResult.Failure<bool>(ErrorCodes.OwnerImmutable);
        }

        AuthorizationDecision decision = authorizationService.Authorize(actor, new(ResourceKind.User, userId), ServitiumAction.ManageUsers);
        return !decision.Allowed
            ? ApplicationResult.Failure<bool>(decision.Code!)
            : await store.SoftDeleteUserAsync(userId, cancellationToken).ConfigureAwait(false)
            ? ApplicationResult.Success(true)
            : ApplicationResult.Failure<bool>(ErrorCodes.Conflict);
    }

    /// <summary>设置用户实例授权。</summary>
    public async Task<ApplicationResult<IReadOnlySet<Guid>>> ReplaceGrantsAsync(
        AuthorizationPrincipal actor,
        Guid userId,
        IReadOnlySet<Guid> grants,
        CancellationToken cancellationToken)
    {
        AuthorizationDecision decision = authorizationService.Authorize(actor, new(ResourceKind.User, userId), ServitiumAction.ManageUsers);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<IReadOnlySet<Guid>>(decision.Code!);
        }

        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            return ApplicationResult.Failure<IReadOnlySet<Guid>>(ErrorCodes.NotFound);
        }

        await store.ReplaceInstanceGrantsAsync(userId, grants, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(grants);
    }

    /// <summary>设置用户 Windows SID 绑定。</summary>
    public async Task<ApplicationResult<StoredWindowsIdentity>> SetWindowsIdentityAsync(
        AuthorizationPrincipal actor,
        Guid userId,
        string sid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(sid);
        AuthorizationDecision decision = authorizationService.Authorize(actor, new(ResourceKind.User, userId), ServitiumAction.ManageUsers);
        if (!decision.Allowed)
        {
            return ApplicationResult.Failure<StoredWindowsIdentity>(decision.Code!);
        }

        string normalized = sid.Trim().ToUpperInvariant();
        if (normalized.Length is < 8 or > 184 || !normalized.StartsWith("S-1-", StringComparison.Ordinal) || normalized.Any(character => !char.IsAsciiDigit(character) && character != '-' && character != 'S'))
        {
            return ApplicationResult.Failure<StoredWindowsIdentity>(ErrorCodes.ValidationFailed);
        }

        if (await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            return ApplicationResult.Failure<StoredWindowsIdentity>(ErrorCodes.NotFound);
        }

        StoredWindowsIdentity binding = new(guidFactory.Create(), userId, normalized, clock.UtcNow);
        return await store.TrySetWindowsIdentityAsync(binding, cancellationToken).ConfigureAwait(false)
            ? ApplicationResult.Success(binding)
            : ApplicationResult.Failure<StoredWindowsIdentity>(ErrorCodes.Conflict);
    }

    /// <summary>使用本机一次性换票创建正式会话。</summary>
    public async Task<ApplicationResult<AuthenticationResult>> ExchangeLocalSessionAsync(
        string token,
        string? codeVerifier,
        CancellationToken cancellationToken)
    {
        string tokenHash;
        if (codeVerifier is null)
        {
            tokenHash = HashToken(token);
        }
        else
        {
            if (!IsPkceVerifier(codeVerifier))
            {
                return ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.Unauthenticated);
            }

            tokenHash = HashToken(token + "." + PkceChallenge(codeVerifier));
        }

        StoredLocalSessionTicket? ticket = await store.ConsumeLocalSessionTicketAsync(tokenHash, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            return ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.Unauthenticated);
        }

        StoredUser? user = await store.FindUserAsync(ticket.UserId, cancellationToken).ConfigureAwait(false);
        return user is null || user.IsDeleted || user.Role == Role.Disabled
            ? ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.AccountDisabled)
            : ApplicationResult.Success(await CreateSessionAsync(user, false, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>由经过验证的本机管道对端签发 60 秒一次性换票。</summary>
    public async Task<string> IssueLocalSessionTicketAsync(Guid userId, CancellationToken cancellationToken)
    {
        string token = tokenGenerator.Generate(32);
        await store.AddLocalSessionTicketAsync(new(guidFactory.Create(), userId, HashToken(token), clock.UtcNow.AddSeconds(60), false), cancellationToken).ConfigureAwait(false);
        return token;
    }

    /// <summary>为完成 OIDC 的 Desktop 签发与 PKCE challenge 绑定的 60 秒一次性换票。</summary>
    public async Task<ApplicationResult<string>> IssueDesktopOidcTicketAsync(
        Guid userId,
        string codeChallenge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codeChallenge);
        if (!IsPkceChallenge(codeChallenge))
        {
            return ApplicationResult.Failure<string>(ErrorCodes.ValidationFailed);
        }

        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            return ApplicationResult.Failure<string>(ErrorCodes.Unauthenticated);
        }

        if (user.Role == Role.Disabled)
        {
            return ApplicationResult.Failure<string>(ErrorCodes.AccountDisabled);
        }

        string token = tokenGenerator.Generate(32);
        await store.AddLocalSessionTicketAsync(new(
            guidFactory.Create(),
            userId,
            HashToken(token + "." + codeChallenge),
            clock.UtcNow.AddSeconds(60),
            false), cancellationToken).ConfigureAwait(false);
        await store.UpdateUserAsync(user with { LastSignedInAt = clock.UtcNow }, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(token);
    }

    /// <summary>为已经完成外部身份验证的用户创建正式会话。</summary>
    public async Task<ApplicationResult<AuthenticationResult>> CreateExternalSessionAsync(Guid userId, CancellationToken cancellationToken)
    {
        StoredUser? user = await store.FindUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            return ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.Unauthenticated);
        }

        if (user.Role == Role.Disabled)
        {
            return ApplicationResult.Failure<AuthenticationResult>(ErrorCodes.AccountDisabled);
        }

        StoredUser signedIn = user with { LastSignedInAt = clock.UtcNow };
        await store.UpdateUserAsync(signedIn, cancellationToken).ConfigureAwait(false);
        return ApplicationResult.Success(await CreateSessionAsync(signedIn, false, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>获取用户统计。</summary>
    public Task<(int Total, int Enabled, int Disabled, int WithTotp, int WithExternalIdentity)> GetUserOverviewAsync(CancellationToken cancellationToken)
    {
        return store.GetUserOverviewAsync(cancellationToken);
    }

    /// <summary>校验密码强度。</summary>
    public static string? ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return string.IsNullOrWhiteSpace(password) || password.Length < 12 || WeakPasswords.Contains(password)
            ? ErrorCodes.WeakPassword
            : null;
    }

    private static bool IsPkceVerifier(string value)
    {
        return value.Length is >= 43 and <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');
    }

    private static bool IsPkceChallenge(string value)
    {
        return value.Length == 43 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static string PkceChallenge(string verifier)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>以 SHA-256 哈希不透明令牌。</summary>
    public static string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static string NormalizeUserName(string userName)
    {
        return userName.Trim().ToUpperInvariant();
    }

    private static string NormalizeLanguage(string language)
    {
        return language is "en-US" ? "en-US" : "zh-Hans";
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
    {
        return left <= right ? left : right;
    }

    private bool VerifyTotp(StoredUser user, string code)
    {
        if (user.ProtectedTotpSecret is null)
        {
            return false;
        }

        byte[] plaintext = secretProtector.Unprotect(user.ProtectedTotpSecret.Value.Span);
        try
        {
            return totpService.Verify(Encoding.ASCII.GetString(plaintext), code, clock.UtcNow);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task<AuthenticationResult> CreateSessionAsync(StoredUser user, bool restricted, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        string token = tokenGenerator.Generate(32);
        StoredSession session = new(
            guidFactory.Create(),
            user.Id,
            HashToken(token),
            now,
            now,
            now.Add(restricted ? TimeSpan.FromMinutes(5) : options.SessionLifetime),
            now.Add(restricted ? TimeSpan.FromMinutes(5) : options.AbsoluteSessionLifetime),
            user.SecurityStamp,
            restricted);
        await store.AddSessionAsync(session, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<Guid> grants = await store.ListInstanceGrantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new(token, session, user, grants);
    }

    private string GenerateRecoveryCode()
    {
        string value = tokenGenerator.Generate(16).Replace('-', 'A').Replace('_', 'B').ToUpperInvariant();
        return value[..6] + "-" + value[6..12];
    }
}

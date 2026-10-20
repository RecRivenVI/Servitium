using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Results;
using Servitium.Application.Identity;
using Servitium.Domain.Identity;

using Xunit;

namespace Servitium.Application.Tests;

public sealed class IdentityApplicationServiceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-220")]
    public async Task 密码登录与滑动会话_执行规范化重哈希过期和资料更新()
    {
        TestContext context = new();
        Assert.False(await context.Service.IsInitializedAsync(CancellationToken.None).ConfigureAwait(true));
        Assert.Equal("weak-password", (await context.Service.InitializeAsync("owner", "Owner", "password1234", "en-US", CancellationToken.None).ConfigureAwait(true)).ErrorCode);

        ApplicationResult<StoredUser> initialized = await context.Service.InitializeAsync(" owner ", " Owner ", "Correct-Horse-1", "en-US", CancellationToken.None).ConfigureAwait(true);
        Assert.True(initialized.Succeeded);
        Assert.Equal("OWNER", initialized.Value!.UserName);
        Assert.Equal("Owner", initialized.Value.DisplayName);
        Assert.Equal("en-US", initialized.Value.PreferredLanguage);
        Assert.True(await context.Service.IsInitializedAsync(CancellationToken.None).ConfigureAwait(true));
        Assert.Equal("already-initialized", (await context.Service.InitializeAsync("other", "Other", "Another-Pass-1", "zh-Hans", CancellationToken.None).ConfigureAwait(true)).ErrorCode);

        Assert.Equal("unauthenticated", (await context.Service.SignInAsync("missing", "Correct-Horse-1", CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        context.PasswordHasher.Rehash = true;
        ApplicationResult<AuthenticationResult> signedIn = await context.Service.SignInAsync("Owner", "Correct-Horse-1", CancellationToken.None).ConfigureAwait(true);
        Assert.True(signedIn.Succeeded);
        Assert.False(signedIn.Value!.Session.IsRestricted);
        _ = Assert.Single(context.Store.Sessions);
        Assert.False(context.PasswordHasher.Rehash);

        context.Clock.UtcNow = Epoch.AddMinutes(2);
        AuthenticationResult? authenticated = await context.Service.AuthenticateSessionAsync(signedIn.Value.SessionToken, CancellationToken.None).ConfigureAwait(true);
        Assert.NotNull(authenticated);
        Assert.Equal(context.Clock.UtcNow, authenticated.Session.LastSeenAt);

        Assert.Equal("validation-failed", (await context.Service.UpdateProfileAsync(initialized.Value.Id, "\u0001", "en-US", CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        ApplicationResult<StoredUser> profile = await context.Service.UpdateProfileAsync(initialized.Value.Id, " New Name ", "unsupported", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("New Name", profile.Value!.DisplayName);
        Assert.Equal("zh-Hans", profile.Value.PreferredLanguage);
        Assert.Equal("unauthenticated", (await context.Service.ChangePasswordAsync(initialized.Value.Id, signedIn.Value.Session.Id, "wrong", "Another-Pass-1", CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        Assert.Equal("weak-password", (await context.Service.ChangePasswordAsync(initialized.Value.Id, signedIn.Value.Session.Id, "Correct-Horse-1", "short", CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        Assert.True((await context.Service.ChangePasswordAsync(initialized.Value.Id, signedIn.Value.Session.Id, "Correct-Horse-1", "Another-Pass-1", CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.Equal(1, context.Store.RevokeOtherSessionsCount);

        Assert.Null(await context.Service.AuthenticateSessionAsync(signedIn.Value.SessionToken, CancellationToken.None).ConfigureAwait(true));
        ApplicationResult<AuthenticationResult> replacement = await context.Service.SignInAsync("owner", "Another-Pass-1", CancellationToken.None).ConfigureAwait(true);
        context.Clock.UtcNow = Epoch.AddDays(2);
        Assert.Null(await context.Service.AuthenticateSessionAsync(replacement.Value!.SessionToken, CancellationToken.None).ConfigureAwait(true));
        Assert.Contains(replacement.Value.Session.Id, context.Store.RevokedSessions);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-221")]
    public async Task Totp与ApiKey_轮换受限会话并拒绝失效认证材料()
    {
        TestContext context = new();
        StoredUser owner = (await context.Service.InitializeAsync("owner", "Owner", "Correct-Horse-1", "zh-Hans", CancellationToken.None).ConfigureAwait(true)).Value!;

        ApplicationResult<string> setup = await context.Service.BeginTotpSetupAsync(owner.Id, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("SYNTHETIC-SECRET", setup.Value);
        Assert.Equal("totp-invalid", (await context.Service.ConfirmTotpAsync(owner.Id, "000000", CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        ApplicationResult<IReadOnlyList<string>> confirmed = await context.Service.ConfirmTotpAsync(owner.Id, "123456", CancellationToken.None).ConfigureAwait(true);
        Assert.True(confirmed.Succeeded);
        Assert.Equal(10, confirmed.Value!.Count);
        Assert.All(confirmed.Value, value => Assert.Matches("^[A-Z0-9]{6}-[A-Z0-9]{6}$", value));

        ApplicationResult<AuthenticationResult> pending = await context.Service.SignInAsync("owner", "Correct-Horse-1", CancellationToken.None).ConfigureAwait(true);
        Assert.True(pending.Value!.Session.IsRestricted);
        Assert.Equal("totp-invalid", (await context.Service.CompleteTotpSignInAsync(pending.Value, "000000", CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        ApplicationResult<AuthenticationResult> completed = await context.Service.CompleteTotpSignInAsync(pending.Value, "123456", CancellationToken.None).ConfigureAwait(true);
        Assert.True(completed.Succeeded);
        Assert.False(completed.Value!.Session.IsRestricted);
        Assert.Contains(pending.Value.Session.Id, context.Store.RevokedSessions);

        context.Options.Update(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), false);
        Assert.Equal("api-key-disabled", (await context.Service.CreateApiKeyAsync(TestContext.Owner(owner.Id), "automation", "instances:read", null, CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        Assert.Null(await context.Service.AuthenticateApiKeyAsync("disabled", CancellationToken.None).ConfigureAwait(true));
        context.Options.Update(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), true);
        ApplicationResult<ApiKeyCreationResult> createdKey = await context.Service.CreateApiKeyAsync(TestContext.Owner(owner.Id), " automation ", " instances:read ", Epoch.AddHours(1), CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("automation", createdKey.Value!.ApiKey.Name);
        AuthenticationResult? keyResult = await context.Service.AuthenticateApiKeyAsync(createdKey.Value.PlaintextKey, CancellationToken.None).ConfigureAwait(true);
        Assert.NotNull(keyResult);
        Assert.Equal(createdKey.Value.ApiKey.Id, keyResult.Session.Id);
        Assert.Equal(context.Clock.UtcNow, context.Store.ApiKeys[createdKey.Value.ApiKey.Id].LastUsedAt);

        context.Store.ApiKeys[createdKey.Value.ApiKey.Id] = createdKey.Value.ApiKey with { IsRevoked = true };
        Assert.Null(await context.Service.AuthenticateApiKeyAsync(createdKey.Value.PlaintextKey, CancellationToken.None).ConfigureAwait(true));
        Assert.True((await context.Service.RemoveTotpAsync(owner.Id, CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.False(context.Store.Users[owner.Id].TotpConfirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-222")]
    public async Task 用户管理与本机换票_统一授权并实施一次性和Pkce边界()
    {
        TestContext context = new();
        StoredUser owner = (await context.Service.InitializeAsync("owner", "Owner", "Correct-Horse-1", "zh-Hans", CancellationToken.None).ConfigureAwait(true)).Value!;
        AuthorizationPrincipal actor = TestContext.Owner(owner.Id);
        AuthorizationPrincipal ordinary = new(Guid.NewGuid(), Role.User, new HashSet<Guid>());

        Assert.Equal("forbidden", (await context.Service.CreateUserAsync(ordinary, "user", "User", "Regular-Pass-1", Role.User, CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        Assert.Equal("owner-immutable", (await context.Service.CreateUserAsync(actor, "owner2", "Owner 2", "Regular-Pass-1", Role.Owner, CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        StoredUser user = (await context.Service.CreateUserAsync(actor, " user ", " User ", "Regular-Pass-1", Role.User, CancellationToken.None).ConfigureAwait(true)).Value!;
        Assert.Equal("user-name-taken", (await context.Service.CreateUserAsync(actor, "USER", "Other", "Regular-Pass-2", Role.User, CancellationToken.None).ConfigureAwait(true)).ErrorCode);

        ApplicationResult<StoredUser> updated = await context.Service.UpdateUserAsync(actor, user.Id, " Operator ", Role.Administrator, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(Role.Administrator, updated.Value!.Role);
        Assert.Equal("owner-immutable", (await context.Service.UpdateUserAsync(actor, owner.Id, "Owner", Role.Owner, CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        Assert.True((await context.Service.ResetPasswordAsync(actor, user.Id, "Replacement-1", CancellationToken.None).ConfigureAwait(true)).Succeeded);
        AuthorizationPrincipal administrator = new(user.Id, Role.Administrator, new HashSet<Guid>());
        Assert.Equal("owner-immutable", (await context.Service.ResetPasswordAsync(administrator, owner.Id, "Replacement-2", CancellationToken.None).ConfigureAwait(true)).ErrorCode);

        var instanceId = Guid.Parse("10000000-0000-4000-8000-000000000001");
        ApplicationResult<IReadOnlySet<Guid>> grants = await context.Service.ReplaceGrantsAsync(actor, user.Id, new HashSet<Guid> { instanceId }, CancellationToken.None).ConfigureAwait(true);
        Assert.Contains(instanceId, grants.Value!);
        Assert.Equal("validation-failed", (await context.Service.SetWindowsIdentityAsync(actor, user.Id, "not-a-sid", CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        ApplicationResult<StoredWindowsIdentity> binding = await context.Service.SetWindowsIdentityAsync(actor, user.Id, " s-1-5-21-1000 ", CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("S-1-5-21-1000", binding.Value!.Sid);
        Assert.Equal("conflict", (await context.Service.SetWindowsIdentityAsync(actor, user.Id, "S-1-5-21-1000", CancellationToken.None).ConfigureAwait(true)).ErrorCode);

        Assert.True((await context.Service.SetUserDisabledAsync(actor, user.Id, true, CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.Equal("account-disabled", (await context.Service.CreateExternalSessionAsync(user.Id, CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        Assert.True((await context.Service.SetUserDisabledAsync(actor, user.Id, false, CancellationToken.None).ConfigureAwait(true)).Succeeded);

        string localTicket = await context.Service.IssueLocalSessionTicketAsync(user.Id, CancellationToken.None).ConfigureAwait(true);
        Assert.True((await context.Service.ExchangeLocalSessionAsync(localTicket, null, CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.Equal("unauthenticated", (await context.Service.ExchangeLocalSessionAsync(localTicket, null, CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        Assert.Equal("validation-failed", (await context.Service.IssueDesktopOidcTicketAsync(user.Id, "short", CancellationToken.None).ConfigureAwait(true)).ErrorCode);

        string verifier = new('a', 43);
        string challenge = PkceChallenge(verifier);
        ApplicationResult<string> oidcTicket = await context.Service.IssueDesktopOidcTicketAsync(user.Id, challenge, CancellationToken.None).ConfigureAwait(true);
        Assert.True(oidcTicket.Succeeded);
        Assert.True((await context.Service.ExchangeLocalSessionAsync(oidcTicket.Value!, verifier, CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.Equal("unauthenticated", (await context.Service.ExchangeLocalSessionAsync("missing", "bad", CancellationToken.None).ConfigureAwait(true)).ErrorCode);
        Assert.True((await context.Service.CreateExternalSessionAsync(user.Id, CancellationToken.None).ConfigureAwait(true)).Succeeded);

        Assert.Equal(2, (await context.Service.GetUserOverviewAsync(CancellationToken.None).ConfigureAwait(true)).Total);
        Assert.True((await context.Service.DeleteUserAsync(actor, user.Id, CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.Equal("owner-immutable", (await context.Service.DeleteUserAsync(actor, owner.Id, CancellationToken.None).ConfigureAwait(true)).ErrorCode);
    }

    private static string PkceChallenge(string verifier)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class TestContext
    {
        public FakeStore Store { get; } = new();
        public FakePasswordHasher PasswordHasher { get; } = new();
        public MutableClock Clock { get; } = new() { UtcNow = Epoch };
        public IdentityServiceOptions Options { get; } = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), true);
        public IdentityApplicationService Service { get; }

        public TestContext()
        {
            Service = new(
                Store,
                PasswordHasher,
                new FakeTotpService(),
                new FakeProtector(),
                new SequentialTokenGenerator(),
                Clock,
                new SequentialGuidFactory(),
                new AuthorizationService(),
                Options);
        }

        public static AuthorizationPrincipal Owner(Guid id)
        {
            return new(id, Role.Owner, new HashSet<Guid>());
        }
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class SequentialGuidFactory : IGuidFactory
    {
        private int _next;
        public Guid Create()
        {
            byte[] bytes = new byte[16];
            BitConverter.GetBytes(Interlocked.Increment(ref _next)).CopyTo(bytes, 0);
            return new Guid(bytes);
        }
    }

    private sealed class SequentialTokenGenerator : ITokenGenerator
    {
        private int _next;
        public string Generate(int entropyBytes)
        {
            return $"TOKEN{Interlocked.Increment(ref _next):D7}ABCDEFGHIJKLMN";
        }
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public bool Rehash { get; set; }
        public string Hash(string password)
        {
            Rehash = false;
            return "hash:" + password;
        }

        public bool Verify(string password, string encodedHash)
        {
            return encodedHash == "hash:" + password;
        }

        public bool NeedsRehash(string encodedHash)
        {
            return Rehash;
        }
    }

    private sealed class FakeTotpService : ITotpService
    {
        public string GenerateSecret()
        {
            return "SYNTHETIC-SECRET";
        }

        public bool Verify(string secret, string code, DateTimeOffset now)
        {
            return secret == "SYNTHETIC-SECRET" && code == "123456";
        }
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            return plaintext.ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
        {
            return protectedData.ToArray();
        }
    }

    private sealed class FakeStore : IIdentityStore
    {
        public Dictionary<Guid, StoredUser> Users { get; } = [];
        public Dictionary<Guid, StoredSession> Sessions { get; } = [];
        public Dictionary<Guid, StoredApiKey> ApiKeys { get; } = [];
        public HashSet<Guid> RevokedSessions { get; } = [];
        public int RevokeOtherSessionsCount { get; private set; }
        private Dictionary<Guid, IReadOnlySet<Guid>> Grants { get; } = [];
        private Dictionary<Guid, StoredWindowsIdentity> WindowsIdentities { get; } = [];
        private Dictionary<Guid, StoredLocalSessionTicket> LocalTickets { get; } = [];
        private Dictionary<Guid, StoredExternalIdentity> ExternalIdentities { get; } = [];
        private Dictionary<Guid, StoredOidcState> OidcStates { get; } = [];

        public Task<bool> HasUsersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Users.Count != 0);
        }

        public Task<bool> TryCreateInitialOwnerAsync(StoredUser owner, CancellationToken cancellationToken)
        {
            if (Users.Count != 0)
            {
                return Task.FromResult(false);
            }

            Users.Add(owner.Id, owner);
            return Task.FromResult(true);
        }

        public Task<StoredUser?> FindUserAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Users.GetValueOrDefault(id));
        }

        public Task<StoredUser?> FindUserByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
        {
            return Task.FromResult(Users.Values.SingleOrDefault(user => user.UserName == normalizedUserName));
        }

        public Task<IReadOnlyList<StoredUser>> ListUsersAsync(Guid? afterId, int limit, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<StoredUser>>([.. Users.Values.OrderBy(user => user.Id).Where(user => afterId is null || user.Id.CompareTo(afterId.Value) > 0).Take(limit)]);
        }

        public Task<bool> TryCreateUserAsync(StoredUser user, CancellationToken cancellationToken)
        {
            if (Users.Values.Any(existing => existing.UserName == user.UserName))
            {
                return Task.FromResult(false);
            }

            Users.Add(user.Id, user);
            return Task.FromResult(true);
        }

        public Task UpdateUserAsync(StoredUser user, CancellationToken cancellationToken)
        {
            Users[user.Id] = user;
            return Task.CompletedTask;
        }

        public Task<StoredUser?> SetUserDisabledAsync(Guid userId, bool disabled, CancellationToken cancellationToken)
        {
            if (!Users.TryGetValue(userId, out StoredUser? user) || user.Role == Role.Owner)
            {
                return Task.FromResult<StoredUser?>(null);
            }

            StoredUser updated = user with { Role = disabled ? Role.Disabled : Role.User, SecurityStamp = user.SecurityStamp + 1 };
            Users[userId] = updated;
            return Task.FromResult<StoredUser?>(updated);
        }

        public Task<bool> SoftDeleteUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            if (!Users.TryGetValue(userId, out StoredUser? user))
            {
                return Task.FromResult(false);
            }

            Users[userId] = user with { IsDeleted = true };
            return Task.FromResult(true);
        }

        public Task<(int Total, int Enabled, int Disabled, int WithTotp, int WithExternalIdentity)> GetUserOverviewAsync(CancellationToken cancellationToken)
        {
            StoredUser[] users = [.. Users.Values.Where(user => !user.IsDeleted)];
            return Task.FromResult((users.Length, users.Count(user => user.Role != Role.Disabled), users.Count(user => user.Role == Role.Disabled), users.Count(user => user.TotpConfirmed), ExternalIdentities.Count));
        }

        public Task<int> CountActiveSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            return Task.FromResult(Sessions.Values.Count(session => session.ExpiresAt > now));
        }

        public Task<IReadOnlySet<Guid>> ListInstanceGrantsAsync(Guid userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Grants.GetValueOrDefault(userId) ?? new HashSet<Guid>());
        }

        public Task ReplaceInstanceGrantsAsync(Guid userId, IReadOnlySet<Guid> instanceIds, CancellationToken cancellationToken)
        {
            Grants[userId] = new HashSet<Guid>(instanceIds);
            return Task.CompletedTask;
        }

        public Task<int> RemoveInstanceGrantsAsync(Guid instanceId, CancellationToken cancellationToken)
        {
            int changed = 0;
            foreach ((Guid userId, IReadOnlySet<Guid> grants) in Grants.ToArray())
            {
                var updated = new HashSet<Guid>(grants);
                if (updated.Remove(instanceId)) { Grants[userId] = updated; changed++; }
            }
            return Task.FromResult(changed);
        }

        public Task AddSessionAsync(StoredSession session, CancellationToken cancellationToken) { Sessions[session.Id] = session; return Task.CompletedTask; }
        public Task<StoredSession?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken)
        {
            return Task.FromResult(Sessions.Values.SingleOrDefault(session => session.TokenHash == tokenHash && !RevokedSessions.Contains(session.Id)));
        }

        public Task TouchSessionAsync(Guid sessionId, DateTimeOffset lastSeenAt, DateTimeOffset expiresAt, CancellationToken cancellationToken) { Sessions[sessionId] = Sessions[sessionId] with { LastSeenAt = lastSeenAt, ExpiresAt = expiresAt }; return Task.CompletedTask; }
        public Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken) { _ = RevokedSessions.Add(sessionId); return Task.CompletedTask; }
        public Task RevokeOtherSessionsAsync(Guid userId, Guid retainedSessionId, CancellationToken cancellationToken) { RevokeOtherSessionsCount++; foreach (StoredSession session in Sessions.Values.Where(session => session.UserId == userId && session.Id != retainedSessionId)) { _ = RevokedSessions.Add(session.Id); } return Task.CompletedTask; }
        public Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken) { foreach (StoredSession session in Sessions.Values.Where(session => session.UserId == userId)) { _ = RevokedSessions.Add(session.Id); } return Task.CompletedTask; }
        public Task<IReadOnlyList<StoredSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<StoredSession>>([.. Sessions.Values.Where(session => session.UserId == userId && !RevokedSessions.Contains(session.Id))]);
        }

        public Task TransferOwnershipAsync(Guid currentOwnerId, Guid newOwnerId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task AddApiKeyAsync(StoredApiKey apiKey, CancellationToken cancellationToken) { ApiKeys[apiKey.Id] = apiKey; return Task.CompletedTask; }
        public Task<StoredApiKey?> FindApiKeyAsync(string keyHash, CancellationToken cancellationToken)
        {
            return Task.FromResult(ApiKeys.Values.SingleOrDefault(key => key.KeyHash == keyHash));
        }

        public Task TouchApiKeyAsync(Guid apiKeyId, DateTimeOffset usedAt, CancellationToken cancellationToken) { ApiKeys[apiKeyId] = ApiKeys[apiKeyId] with { LastUsedAt = usedAt }; return Task.CompletedTask; }
        public Task RevokeApiKeyAsync(Guid apiKeyId, Guid ownerUserId, CancellationToken cancellationToken) { if (ApiKeys.TryGetValue(apiKeyId, out StoredApiKey? key) && key.UserId == ownerUserId) { ApiKeys[apiKeyId] = key with { IsRevoked = true }; } return Task.CompletedTask; }
        public Task<IReadOnlyList<StoredApiKey>> ListApiKeysAsync(Guid userId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<StoredApiKey>>([.. ApiKeys.Values.Where(key => key.UserId == userId)]);
        }

        public Task ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyList<StoredRecoveryCode> codes, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<bool> ConsumeRecoveryCodeAsync(Guid userId, string codeHash, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task<StoredExternalIdentity?> FindExternalIdentityAsync(Guid userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(ExternalIdentities.GetValueOrDefault(userId));
        }

        public Task<StoredExternalIdentity?> FindExternalIdentityAsync(string issuer, string subject, CancellationToken cancellationToken)
        {
            return Task.FromResult(ExternalIdentities.Values.SingleOrDefault(identity => identity.Issuer == issuer && identity.Subject == subject));
        }

        public Task<bool> TrySetExternalIdentityAsync(StoredExternalIdentity identity, CancellationToken cancellationToken) { if (ExternalIdentities.Values.Any(existing => existing.Issuer == identity.Issuer && existing.Subject == identity.Subject)) { return Task.FromResult(false); } ExternalIdentities[identity.UserId] = identity; return Task.FromResult(true); }
        public Task RemoveExternalIdentityAsync(Guid userId, CancellationToken cancellationToken) { _ = ExternalIdentities.Remove(userId); return Task.CompletedTask; }
        public Task<StoredWindowsIdentity?> FindWindowsIdentityAsync(Guid userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(WindowsIdentities.GetValueOrDefault(userId));
        }

        public Task<StoredWindowsIdentity?> FindWindowsIdentityBySidAsync(string sid, CancellationToken cancellationToken)
        {
            return Task.FromResult(WindowsIdentities.Values.SingleOrDefault(identity => identity.Sid == sid));
        }

        public Task<bool> TrySetWindowsIdentityAsync(StoredWindowsIdentity identity, CancellationToken cancellationToken) { if (WindowsIdentities.Values.Any(existing => existing.Sid == identity.Sid)) { return Task.FromResult(false); } WindowsIdentities[identity.UserId] = identity; return Task.FromResult(true); }
        public Task AddOidcStateAsync(StoredOidcState state, CancellationToken cancellationToken) { OidcStates[state.Id] = state; return Task.CompletedTask; }
        public Task<StoredOidcState?> ConsumeOidcStateAsync(string stateHash, DateTimeOffset now, CancellationToken cancellationToken) { StoredOidcState? state = OidcStates.Values.SingleOrDefault(candidate => candidate.StateHash == stateHash && !candidate.Consumed && candidate.ExpiresAt > now); if (state is not null) { OidcStates[state.Id] = state with { Consumed = true }; } return Task.FromResult(state); }
        public Task AddLocalSessionTicketAsync(StoredLocalSessionTicket ticket, CancellationToken cancellationToken) { LocalTickets[ticket.Id] = ticket; return Task.CompletedTask; }
        public Task<StoredLocalSessionTicket?> ConsumeLocalSessionTicketAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken) { StoredLocalSessionTicket? ticket = LocalTickets.Values.SingleOrDefault(candidate => candidate.TokenHash == tokenHash && !candidate.Consumed && candidate.ExpiresAt > now); if (ticket is not null) { LocalTickets[ticket.Id] = ticket with { Consumed = true }; } return Task.FromResult(ticket); }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Servitium.Application.Identity;
using Servitium.Domain.Identity;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Infrastructure.Identity;

internal sealed class SqliteIdentityStore(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    SqliteWriteCoordinator writeQueue) : IIdentityStore
{
    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Users.AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TryCreateInitialOwnerAsync(StoredUser owner, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                if (await context.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                _ = context.Users.Add(ToRow(owner));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<StoredUser?> FindUserAsync(Guid id, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        UserRow? row = await context.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    public async Task<StoredUser?> FindUserByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        UserRow? row = await context.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserName == normalizedUserName, cancellationToken).ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    public async Task<IReadOnlyList<StoredUser>> ListUsersAsync(Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        UserRow[] rows = await context.Users.AsNoTracking().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string? cursor = afterId?.ToString("D");
        return [.. rows
            .Where(row => cursor is null || StringComparer.Ordinal.Compare(row.Id.ToString("D"), cursor) > 0)
            .OrderBy(row => row.Id.ToString("D"), StringComparer.Ordinal)
            .Take(limit)
            .Select(FromRow)];
    }

    public Task<bool> TryCreateUserAsync(StoredUser user, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                if (await context.Users.AnyAsync(row => row.UserName == user.UserName, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                _ = context.Users.Add(ToRow(user));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task UpdateUserAsync(StoredUser user, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                UserRow row = await context.Users.SingleAsync(candidate => candidate.Id == user.Id, cancellationToken).ConfigureAwait(false);
                Copy(user, row);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<StoredUser?> SetUserDisabledAsync(Guid userId, bool disabled, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                UserRow? row = await context.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken).ConfigureAwait(false);
                if (row is null || row.IsDeleted || row.Role == (int)Role.Owner)
                {
                    return null;
                }

                if (disabled && row.Role != (int)Role.Disabled)
                {
                    row.PreviousRole = row.Role;
                    row.Role = (int)Role.Disabled;
                    row.SecurityStamp++;
                }
                else if (!disabled && row.Role == (int)Role.Disabled)
                {
                    row.Role = row.PreviousRole is (int)Role.Administrator or (int)Role.User ? row.PreviousRole : (int)Role.User;
                    row.SecurityStamp++;
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return FromRow(row);
            },
            cancellationToken);
    }

    public Task<bool> SoftDeleteUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction =
                    await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                UserRow? user = await context.Users.SingleOrDefaultAsync(row => row.Id == userId, cancellationToken).ConfigureAwait(false);
                if (user is null || user.IsDeleted || user.Role == (int)Role.Owner)
                {
                    return false;
                }

                user.IsDeleted = true;
                user.PasswordHash = "!deleted:" + user.Id.ToString("N");
                user.ProtectedTotpSecret = null;
                user.TotpConfirmed = false;
                user.SecurityStamp++;
                _ = await context.Sessions.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.ApiKeys.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.InstanceGrants.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.RecoveryCodes.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.ExternalIdentities.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.WindowsIdentities.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.LocalSessionTickets.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.OidcStates.Where(row => row.BindingUserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.DashboardLayouts.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<(int Total, int Enabled, int Disabled, int WithTotp, int WithExternalIdentity)> GetUserOverviewAsync(CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        UserRow[] users = await context.Users.AsNoTracking().Where(row => !row.IsDeleted).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        int external = await context.ExternalIdentities.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        int disabled = users.Count(row => row.Role == (int)Role.Disabled);
        return (users.Length, users.Length - disabled, disabled, users.Count(row => row.TotpConfirmed), external);
    }

    public async Task<int> CountActiveSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SessionRow[] sessions = await context.Sessions.AsNoTracking().Where(row => !row.IsRestricted).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return sessions.Count(row => row.ExpiresAt > now && row.AbsoluteExpiresAt > now);
    }

    public async Task<IReadOnlySet<Guid>> ListInstanceGrantsAsync(Guid userId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        Guid[] ids = await context.InstanceGrants.AsNoTracking()
            .Where(row => row.UserId == userId)
            .Select(row => row.InstanceId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new HashSet<Guid>(ids);
    }

    public Task ReplaceInstanceGrantsAsync(Guid userId, IReadOnlySet<Guid> instanceIds, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                InstanceGrantRow[] existing = await context.InstanceGrants.Where(row => row.UserId == userId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                context.InstanceGrants.RemoveRange(existing);
                context.InstanceGrants.AddRange(instanceIds.Select(id => new InstanceGrantRow { UserId = userId, InstanceId = id }));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<int> RemoveInstanceGrantsAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                return await context.InstanceGrants.Where(row => row.InstanceId == instanceId)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task AddSessionAsync(StoredSession session, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.Sessions.Add(ToRow(session));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<StoredSession?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SessionRow? row = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken).ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    public Task TouchSessionAsync(Guid sessionId, DateTimeOffset lastSeenAt, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.Sessions.Where(row => row.Id == sessionId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.LastSeenAt, lastSeenAt)
                        .SetProperty(row => row.ExpiresAt, expiresAt), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        return DeleteSessionsAsync(row => row.Id == sessionId, cancellationToken);
    }

    public Task RevokeOtherSessionsAsync(Guid userId, Guid retainedSessionId, CancellationToken cancellationToken)
    {
        return DeleteSessionsAsync(row => row.UserId == userId && row.Id != retainedSessionId, cancellationToken);
    }

    public Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return DeleteSessionsAsync(row => row.UserId == userId, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SessionRow[] rows = await context.Sessions.AsNoTracking()
            .Where(row => row.UserId == userId && !row.IsRestricted)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        // SQLite 无法对 DateTimeOffset 排序；先在数据库按用户与会话范围过滤，再按真实时间排序。
        return [.. rows.OrderByDescending(row => row.LastSeenAt).Select(FromRow)];
    }

    public Task TransferOwnershipAsync(Guid currentOwnerId, Guid newOwnerId, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                UserRow current = await context.Users.SingleAsync(row => row.Id == currentOwnerId && row.OwnerMarker == "owner", cancellationToken).ConfigureAwait(false);
                UserRow next = await context.Users.SingleAsync(row => row.Id == newOwnerId && !row.IsDeleted && row.Role != (int)Role.Disabled, cancellationToken).ConfigureAwait(false);
                current.Role = (int)Role.Administrator;
                current.OwnerMarker = null;
                current.SecurityStamp++;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                next.Role = (int)Role.Owner;
                next.OwnerMarker = "owner";
                next.SecurityStamp++;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task AddApiKeyAsync(StoredApiKey apiKey, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.ApiKeys.Add(ToRow(apiKey));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<StoredApiKey?> FindApiKeyAsync(string keyHash, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ApiKeyRow? row = await context.ApiKeys.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.KeyHash == keyHash, cancellationToken).ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    public Task TouchApiKeyAsync(Guid apiKeyId, DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.ApiKeys.Where(row => row.Id == apiKeyId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastUsedAt, usedAt), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task RevokeApiKeyAsync(Guid apiKeyId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.ApiKeys.Where(row => row.Id == apiKeyId && row.UserId == ownerUserId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsRevoked, true), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StoredApiKey>> ListApiKeysAsync(Guid userId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ApiKeyRow[] rows = await context.ApiKeys.AsNoTracking()
            .Where(row => row.UserId == userId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        // 与会话列表相同，DateTimeOffset 的比较在读取当前用户的记录后进行。
        return [.. rows.OrderByDescending(row => row.CreatedAt).Select(FromRow)];
    }

    public Task ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyList<StoredRecoveryCode> codes, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.RecoveryCodes.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                context.RecoveryCodes.AddRange(codes.Select(code => new RecoveryCodeRow
                {
                    Id = code.Id,
                    UserId = code.UserId,
                    CodeHash = code.CodeHash,
                    CreatedAt = code.CreatedAt,
                }));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<bool> ConsumeRecoveryCodeAsync(Guid userId, string codeHash, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                return await context.RecoveryCodes.Where(row => row.UserId == userId && row.CodeHash == codeHash)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) == 1;
            },
            cancellationToken);
    }

    public async Task<StoredExternalIdentity?> FindExternalIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ExternalIdentityRow? row = await context.ExternalIdentities.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToStored(row);
    }

    public async Task<StoredExternalIdentity?> FindExternalIdentityAsync(string issuer, string subject, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ExternalIdentityRow? row = await context.ExternalIdentities.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Issuer == issuer && candidate.Subject == subject, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToStored(row);
    }

    public Task<bool> TrySetExternalIdentityAsync(StoredExternalIdentity identity, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                if (await context.ExternalIdentities.AnyAsync(
                        row => row.Issuer == identity.Issuer && row.Subject == identity.Subject && row.UserId != identity.UserId,
                        cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                _ = await context.ExternalIdentities.Where(row => row.UserId == identity.UserId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = context.ExternalIdentities.Add(new ExternalIdentityRow
                {
                    Id = identity.Id,
                    UserId = identity.UserId,
                    Issuer = identity.Issuer,
                    Subject = identity.Subject,
                    CreatedAt = identity.CreatedAt,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task RemoveExternalIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.ExternalIdentities.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<StoredWindowsIdentity?> FindWindowsIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        WindowsIdentityRow? row = await context.WindowsIdentities.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToStored(row);
    }

    public async Task<StoredWindowsIdentity?> FindWindowsIdentityBySidAsync(string sid, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        WindowsIdentityRow? row = await context.WindowsIdentities.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Sid == sid, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToStored(row);
    }

    public Task<bool> TrySetWindowsIdentityAsync(StoredWindowsIdentity identity, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                if (await context.WindowsIdentities.AnyAsync(row => row.Sid == identity.Sid && row.UserId != identity.UserId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                _ = await context.WindowsIdentities.Where(row => row.UserId == identity.UserId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = context.WindowsIdentities.Add(new WindowsIdentityRow
                {
                    Id = identity.Id,
                    UserId = identity.UserId,
                    Sid = identity.Sid,
                    CreatedAt = identity.CreatedAt,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task AddOidcStateAsync(StoredOidcState state, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.OidcStates.Add(new OidcStateRow
                {
                    Id = state.Id,
                    StateHash = state.StateHash,
                    Nonce = state.Nonce,
                    CodeVerifier = state.CodeVerifier,
                    ReturnUrl = state.ReturnUrl?.AbsoluteUri,
                    BindingUserId = state.BindingUserId,
                    ExpiresAt = state.ExpiresAt,
                    Consumed = state.Consumed,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<StoredOidcState?> ConsumeOidcStateAsync(string stateHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                OidcStateRow? row = await context.OidcStates.SingleOrDefaultAsync(candidate => candidate.StateHash == stateHash && !candidate.Consumed, cancellationToken).ConfigureAwait(false);
                if (row is null || row.ExpiresAt <= now)
                {
                    return null;
                }

                row.Consumed = true;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToStored(row);
            },
            cancellationToken);
    }

    public Task AddLocalSessionTicketAsync(StoredLocalSessionTicket ticket, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.LocalSessionTickets.Add(new LocalSessionTicketRow
                {
                    Id = ticket.Id,
                    UserId = ticket.UserId,
                    TokenHash = ticket.TokenHash,
                    ExpiresAt = ticket.ExpiresAt,
                    Consumed = ticket.Consumed,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<StoredLocalSessionTicket?> ConsumeLocalSessionTicketAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalSessionTicketRow? row = await context.LocalSessionTickets.SingleOrDefaultAsync(
                    candidate => candidate.TokenHash == tokenHash && !candidate.Consumed,
                    cancellationToken).ConfigureAwait(false);
                if (row is null || row.ExpiresAt <= now)
                {
                    return null;
                }

                row.Consumed = true;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new StoredLocalSessionTicket(row.Id, row.UserId, row.TokenHash, row.ExpiresAt, row.Consumed);
            },
            cancellationToken);
    }

    private Task DeleteSessionsAsync(System.Linq.Expressions.Expression<Func<SessionRow, bool>> predicate, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.Sessions.Where(predicate).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static UserRow ToRow(StoredUser user)
    {
        UserRow row = new();
        Copy(user, row);
        return row;
    }

    private static void Copy(StoredUser source, UserRow target)
    {
        target.Id = source.Id;
        target.UserName = source.UserName;
        target.DisplayName = source.DisplayName;
        target.Role = (int)source.Role;
        if (source.Role != Role.Disabled)
        {
            target.PreviousRole = (int)source.Role;
        }
        target.OwnerMarker = source.Role == Role.Owner ? "owner" : null;
        target.PasswordHash = source.PasswordHash;
        target.PreferredLanguage = source.PreferredLanguage;
        target.CreatedAt = source.CreatedAt;
        target.LastSignedInAt = source.LastSignedInAt;
        target.IsDeleted = source.IsDeleted;
        target.ProtectedTotpSecret = source.ProtectedTotpSecret?.ToArray();
        target.TotpConfirmed = source.TotpConfirmed;
        target.SecurityStamp = source.SecurityStamp;
    }

    private static StoredUser FromRow(UserRow row)
    {
        return new(row.Id, row.UserName, row.DisplayName, (Role)row.Role, row.PasswordHash, row.PreferredLanguage, row.CreatedAt, row.LastSignedInAt, row.IsDeleted, row.ProtectedTotpSecret is null ? null : new ReadOnlyMemory<byte>(row.ProtectedTotpSecret), row.TotpConfirmed, row.SecurityStamp);
    }

    private static SessionRow ToRow(StoredSession session)
    {
        return new SessionRow
        {
            Id = session.Id,
            UserId = session.UserId,
            TokenHash = session.TokenHash,
            CreatedAt = session.CreatedAt,
            LastSeenAt = session.LastSeenAt,
            ExpiresAt = session.ExpiresAt,
            AbsoluteExpiresAt = session.AbsoluteExpiresAt,
            SecurityStamp = session.SecurityStamp,
            IsRestricted = session.IsRestricted,
        };
    }

    private static StoredSession FromRow(SessionRow row)
    {
        return new(row.Id, row.UserId, row.TokenHash, row.CreatedAt, row.LastSeenAt, row.ExpiresAt, row.AbsoluteExpiresAt, row.SecurityStamp, row.IsRestricted);
    }

    private static ApiKeyRow ToRow(StoredApiKey apiKey)
    {
        return new ApiKeyRow
        {
            Id = apiKey.Id,
            UserId = apiKey.UserId,
            Name = apiKey.Name,
            KeyHash = apiKey.KeyHash,
            Scope = apiKey.Scope,
            CreatedAt = apiKey.CreatedAt,
            ExpiresAt = apiKey.ExpiresAt,
            LastUsedAt = apiKey.LastUsedAt,
            IsRevoked = apiKey.IsRevoked,
        };
    }

    private static StoredApiKey FromRow(ApiKeyRow row)
    {
        return new(row.Id, row.UserId, row.Name, row.KeyHash, row.Scope, row.CreatedAt, row.ExpiresAt, row.LastUsedAt, row.IsRevoked);
    }

    private static StoredExternalIdentity ToStored(ExternalIdentityRow row)
    {
        return new(row.Id, row.UserId, row.Issuer, row.Subject, row.CreatedAt);
    }

    private static StoredWindowsIdentity ToStored(WindowsIdentityRow row)
    {
        return new(row.Id, row.UserId, row.Sid, row.CreatedAt);
    }

    private static StoredOidcState ToStored(OidcStateRow row)
    {
        return new(row.Id, row.StateHash, row.Nonce, row.CodeVerifier, row.ReturnUrl is null ? null : new Uri(row.ReturnUrl, UriKind.Absolute), row.BindingUserId, row.ExpiresAt, row.Consumed);
    }
}

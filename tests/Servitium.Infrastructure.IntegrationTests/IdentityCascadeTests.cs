using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Servitium.Application.Identity;
using Servitium.Domain.Identity;
using Servitium.Infrastructure.Identity;
using Servitium.Infrastructure.Persistence;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class IdentityCascadeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1080")]
    [Trait("TestId", "INT-1090")]
    public async Task 软删除用户清除认证材料与授权_实例清理移除跨用户授权()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-identity-cascade-{Guid.NewGuid():N}")).FullName;
        SqliteWriteCoordinator coordinator = new();
        try
        {
            DbContextOptions<ServitiumDbContext> options = new DbContextOptionsBuilder<ServitiumDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "server.db") + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            var deletedUser = Guid.NewGuid();
            var retainedUser = Guid.NewGuid();
            var instance = Guid.NewGuid();
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
                context.Users.AddRange(
                    User(deletedUser, "DELETED", Role.User),
                    User(retainedUser, "RETAINED", Role.Administrator));
                _ = context.Sessions.Add(new SessionRow { Id = Guid.NewGuid(), UserId = deletedUser, TokenHash = new string('a', 64) });
                _ = context.ApiKeys.Add(new ApiKeyRow { Id = Guid.NewGuid(), UserId = deletedUser, Name = "key", KeyHash = new string('b', 64), Scope = "*" });
                context.InstanceGrants.AddRange(
                    new InstanceGrantRow { UserId = deletedUser, InstanceId = instance },
                    new InstanceGrantRow { UserId = retainedUser, InstanceId = instance });
                _ = context.RecoveryCodes.Add(new RecoveryCodeRow { Id = Guid.NewGuid(), UserId = deletedUser, CodeHash = new string('c', 64) });
                _ = context.ExternalIdentities.Add(new ExternalIdentityRow { Id = Guid.NewGuid(), UserId = deletedUser, Issuer = "https://issuer.invalid", Subject = "subject" });
                _ = context.WindowsIdentities.Add(new WindowsIdentityRow { Id = Guid.NewGuid(), UserId = deletedUser, Sid = "S-1-5-21-1" });
                _ = context.LocalSessionTickets.Add(new LocalSessionTicketRow { Id = Guid.NewGuid(), UserId = deletedUser, TokenHash = new string('d', 64) });
                _ = context.OidcStates.Add(new OidcStateRow { Id = Guid.NewGuid(), BindingUserId = deletedUser, StateHash = new string('e', 64) });
                _ = context.DashboardLayouts.Add(new DashboardLayoutRow { UserId = deletedUser, Page = "dashboard", Version = 1 });
                _ = await context.SaveChangesAsync().ConfigureAwait(true);
            }

            SqliteIdentityStore store = new(factory, coordinator);
            Assert.True(await store.SoftDeleteUserAsync(deletedUser, CancellationToken.None).ConfigureAwait(true));
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                UserRow deleted = await context.Users.SingleAsync(row => row.Id == deletedUser).ConfigureAwait(true);
                Assert.True(deleted.IsDeleted);
                Assert.False(deleted.TotpConfirmed);
                Assert.Null(deleted.ProtectedTotpSecret);
                Assert.StartsWith("!deleted:", deleted.PasswordHash, StringComparison.Ordinal);
                Assert.False(await context.Sessions.AnyAsync(row => row.UserId == deletedUser).ConfigureAwait(true));
                Assert.False(await context.ApiKeys.AnyAsync(row => row.UserId == deletedUser).ConfigureAwait(true));
                Assert.False(await context.InstanceGrants.AnyAsync(row => row.UserId == deletedUser).ConfigureAwait(true));
                Assert.False(await context.RecoveryCodes.AnyAsync(row => row.UserId == deletedUser).ConfigureAwait(true));
                Assert.False(await context.ExternalIdentities.AnyAsync(row => row.UserId == deletedUser).ConfigureAwait(true));
                Assert.False(await context.WindowsIdentities.AnyAsync(row => row.UserId == deletedUser).ConfigureAwait(true));
                Assert.False(await context.LocalSessionTickets.AnyAsync(row => row.UserId == deletedUser).ConfigureAwait(true));
                Assert.False(await context.OidcStates.AnyAsync(row => row.BindingUserId == deletedUser).ConfigureAwait(true));
                Assert.False(await context.DashboardLayouts.AnyAsync(row => row.UserId == deletedUser).ConfigureAwait(true));
                Assert.True(await context.InstanceGrants.AnyAsync(row => row.UserId == retainedUser && row.InstanceId == instance).ConfigureAwait(true));
            }

            Assert.Equal(1, await store.RemoveInstanceGrantsAsync(instance, CancellationToken.None).ConfigureAwait(true));
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                Assert.False(await context.InstanceGrants.AnyAsync(row => row.InstanceId == instance).ConfigureAwait(true));
            }
        }
        finally
        {
            coordinator.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sqlite账户列表按真实时间排序并保留用户与受限会话过滤()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-identity-lists-{Guid.NewGuid():N}")).FullName;
        using SqliteWriteCoordinator coordinator = new();
        try
        {
            TestContextFactory factory = new(new DbContextOptionsBuilder<ServitiumDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "server.db") + ";Pooling=False").Options);
            Guid user = Guid.NewGuid(), other = Guid.NewGuid();
            Guid olderSession = Guid.NewGuid(), newerSession = Guid.NewGuid(), olderKey = Guid.NewGuid(), newerKey = Guid.NewGuid();
            DateTimeOffset older = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
            DateTimeOffset newer = new(2026, 10, 21, 5, 0, 0, TimeSpan.Zero);
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
                context.Users.AddRange(User(user, "LISTED", Role.User), User(other, "OTHER", Role.User));
                context.Sessions.AddRange(
                    new SessionRow { Id = olderSession, UserId = user, TokenHash = new string('a', 64), LastSeenAt = older },
                    new SessionRow { Id = newerSession, UserId = user, TokenHash = new string('b', 64), LastSeenAt = newer },
                    new SessionRow { Id = Guid.NewGuid(), UserId = user, TokenHash = new string('c', 64), LastSeenAt = newer.AddHours(1), IsRestricted = true },
                    new SessionRow { Id = Guid.NewGuid(), UserId = other, TokenHash = new string('d', 64), LastSeenAt = newer.AddHours(2) });
                context.ApiKeys.AddRange(
                    new ApiKeyRow { Id = olderKey, UserId = user, Name = "Older", KeyHash = new string('e', 64), Scope = "full", CreatedAt = older },
                    new ApiKeyRow { Id = newerKey, UserId = user, Name = "Newer", KeyHash = new string('f', 64), Scope = "full", CreatedAt = newer },
                    new ApiKeyRow { Id = Guid.NewGuid(), UserId = other, Name = "Other", KeyHash = new string('1', 64), Scope = "full", CreatedAt = newer.AddHours(2) });
                _ = await context.SaveChangesAsync().ConfigureAwait(true);
            }
            SqliteIdentityStore store = new(factory, coordinator);
            IReadOnlyList<StoredSession> sessions = await store.ListSessionsAsync(user, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal([newerSession, olderSession], sessions.Select(session => session.Id));
            Assert.All(sessions, session => { Assert.Equal(user, session.UserId); Assert.False(session.IsRestricted); });
            IReadOnlyList<StoredApiKey> keys = await store.ListApiKeysAsync(user, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal([newerKey, olderKey], keys.Select(key => key.Id));
            Assert.All(keys, key => Assert.Equal(user, key.UserId));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static UserRow User(Guid id, string userName, Role role)
    {
        return new UserRow
        {
            Id = id,
            UserName = userName,
            DisplayName = userName,
            Role = (int)role,
            PreviousRole = (int)role,
            OwnerMarker = role == Role.Owner ? "owner" : null,
            PasswordHash = "synthetic",
            PreferredLanguage = "en-US",
            CreatedAt = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
            ProtectedTotpSecret = [1, 2, 3],
            TotpConfirmed = true,
        };
    }

    private sealed class TestContextFactory(DbContextOptions<ServitiumDbContext> options) : IDbContextFactory<ServitiumDbContext>
    {
        public ServitiumDbContext CreateDbContext()
        {
            return new(options);
        }
    }
}

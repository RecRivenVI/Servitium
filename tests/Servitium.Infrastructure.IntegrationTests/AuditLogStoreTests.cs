using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Domain.Auditing;
using Servitium.Infrastructure.Auditing;
using Servitium.Infrastructure.Persistence;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class AuditLogStoreTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1100")]
    [Trait("TestId", "INT-1120")]
    public async Task 双日志_只追加持久化并按最新顺序查询()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-audit-{Guid.NewGuid():N}")).FullName;
        SqliteWriteCoordinator coordinator = new();
        try
        {
            DbContextOptions<ServitiumDbContext> options = new DbContextOptionsBuilder<ServitiumDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "server.db") + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            SqliteLogStore store = new(factory, coordinator);
            var actor = Guid.NewGuid();
            DateTimeOffset first = new(2026, 8, 3, 1, 0, 0, TimeSpan.Zero);
            await store.AppendAsync(
                new ActivityLogEntry(Guid.NewGuid(), actor, "owner", "POST /instances", "instance/one", first, new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = "201" }),
                CancellationToken.None).ConfigureAwait(true);
            await store.AppendAsync(
                new ActivityLogEntry(Guid.NewGuid(), actor, "owner", "POST /hmcl", "channel/stable", first.AddSeconds(1), new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = "201" }),
                CancellationToken.None).ConfigureAwait(true);
            await store.AppendAsync(
                new AuditLogEntry(Guid.NewGuid(), actor, "owner", AuditCategory.Authentication, "POST /auth/login", "auth", first, "success"),
                CancellationToken.None).ConfigureAwait(true);

            IReadOnlyList<ActivityLogEntry> activities = await store.ListActivitiesAsync(100, CancellationToken.None).ConfigureAwait(true);
            IReadOnlyList<AuditLogEntry> audit = await store.ListAuditAsync(100, CancellationToken.None).ConfigureAwait(true);

            Assert.Equal(2, activities.Count);
            Assert.Equal("POST /hmcl", activities[0].Action);
            Assert.Equal("201", activities[0].Properties["status"]);
            _ = Assert.Single(audit);
            Assert.Equal(AuditCategory.Authentication, audit.Single().Category);
            Assert.DoesNotContain(
                typeof(SqliteLogStore).GetMethods(),
                method => method.Name.Contains("Update", StringComparison.Ordinal) || method.Name.Contains("Delete", StringComparison.Ordinal));
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

    private sealed class TestContextFactory(DbContextOptions<ServitiumDbContext> options) : IDbContextFactory<ServitiumDbContext>
    {
        public ServitiumDbContext CreateDbContext()
        {
            return new ServitiumDbContext(options);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Abstractions;
using Servitium.Infrastructure.Auditing;
using Servitium.Infrastructure.Maintenance;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class RetentionMaintenanceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1180")]
    public async Task 保留期清理删除超期数据_保留边界内数据并记录作业自身()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-retention-{Guid.NewGuid():N}")).FullName;
        SqliteWriteCoordinator coordinator = new();
        try
        {
            DateTimeOffset now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
            DbContextOptions<ServitiumDbContext> options = new DbContextOptionsBuilder<ServitiumDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "server.db") + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            var nodeId = Guid.NewGuid();
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
                _ = context.Nodes.Add(new NodeRow { Id = nodeId, Name = "synthetic-node", MachineFingerprint = "synthetic", CreatedAt = now });
                context.ActivityLogs.AddRange(Activity(now.AddDays(-181)), Activity(now.AddDays(-179)));
                context.AuditLogs.AddRange(Audit(now.AddDays(-366)), Audit(now.AddDays(-364)));
                context.Operations.AddRange(Operation(now.AddDays(-31)), Operation(now.AddDays(-29)));
                context.NodeMetrics.AddRange(new NodeMetricRow { NodeId = nodeId, SampledAt = now.AddHours(-25) }, new NodeMetricRow { NodeId = nodeId, SampledAt = now.AddHours(-23) });
                _ = context.IpBans.Add(new IpBanRow { Address = "192.0.2.1", ExpiresAt = now.AddMinutes(-1), ReasonCode = "synthetic" });
                _ = context.IdempotencyRecords.Add(new IdempotencyRow { Key = "expired", RequestHash = new string('a', 64), ExpiresAt = now.AddMinutes(-1) });
                _ = await context.SaveChangesAsync().ConfigureAwait(true);
            }

            SqliteLogStore logs = new(factory, coordinator);
            RetentionMaintenanceService maintenance = new(factory, coordinator, logs, logs, new FixedClock(now), new GuidFactory());
            RetentionCleanupResult result = await maintenance.RunAsync(new RetentionPolicy(), CancellationToken.None).ConfigureAwait(true);

            Assert.Equal(1, result.Activities);
            Assert.Equal(1, result.Audits);
            Assert.Equal(1, result.Operations);
            Assert.Equal(1, result.Metrics);
            Assert.Equal(2, result.ExpiredSecurityState);
            using (ServitiumDbContext context = factory.CreateDbContext())
            {
                Assert.Equal(2, await context.ActivityLogs.CountAsync().ConfigureAwait(true));
                Assert.Equal(2, await context.AuditLogs.CountAsync().ConfigureAwait(true));
                _ = Assert.Single(await context.Operations.ToArrayAsync().ConfigureAwait(true));
                _ = Assert.Single(await context.NodeMetrics.ToArrayAsync().ConfigureAwait(true));
                Assert.False(await context.IpBans.AnyAsync().ConfigureAwait(true));
                Assert.False(await context.IdempotencyRecords.AnyAsync().ConfigureAwait(true));
                Assert.Contains(await context.ActivityLogs.ToArrayAsync().ConfigureAwait(true), row => row.Action == "maintenance.retention");
                Assert.Contains(await context.AuditLogs.ToArrayAsync().ConfigureAwait(true), row => row.Action == "maintenance.audit-retention");
            }

            _ = await Assert.ThrowsAsync<Domain.Primitives.DomainException>(() => maintenance.RunAsync(
                new RetentionPolicy(AuditDays: 89), CancellationToken.None)).ConfigureAwait(true);
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

    private static ActivityLogRow Activity(DateTimeOffset occurredAt)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            ActorNameSnapshot = "synthetic",
            Action = "synthetic",
            Resource = "synthetic",
            OccurredAt = occurredAt,
        };
    }

    private static AuditLogRow Audit(DateTimeOffset occurredAt)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            ActorNameSnapshot = "synthetic",
            Category = "Authentication",
            Action = "synthetic",
            Resource = "synthetic",
            OccurredAt = occurredAt,
            OutcomeCode = "ok",
        };
    }

    private static OperationRow Operation(DateTimeOffset completedAt)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Kind = "synthetic",
            Target = "synthetic",
            Status = "Succeeded",
            CreatedAt = completedAt.AddMinutes(-1),
            CompletedAt = completedAt,
        };
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; } = value;
    }

    private sealed class TestContextFactory(DbContextOptions<ServitiumDbContext> options) : IDbContextFactory<ServitiumDbContext>
    {
        public ServitiumDbContext CreateDbContext()
        {
            return new(options);
        }
    }
}

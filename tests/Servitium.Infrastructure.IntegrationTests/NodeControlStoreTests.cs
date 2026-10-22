using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Servitium.Application.Nodes;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Instances;
using Servitium.Infrastructure.Nodes;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class NodeControlStoreTests
{
    [Fact]
    public async Task 实例历史每分钟保留最新样本_拒绝乱序覆盖且按节点实例与保留期隔离()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-instance-metrics-{Guid.NewGuid():N}")).FullName;
        DbContextOptions<ServitiumDbContext> options = new DbContextOptionsBuilder<ServitiumDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "server.db") + ";Pooling=False").Options;
        TestContextFactory factory = new(options);
        using (ServitiumDbContext context = factory.CreateDbContext()) { await context.Database.MigrateAsync().ConfigureAwait(true); }
        using SqliteWriteCoordinator coordinator = new();
        SqliteNodeControlStore store = new(factory, coordinator, new SystemClock());
        DateTimeOffset now = new(2026, 10, 22, 0, 0, 45, TimeSpan.Zero);
        StoredNode node = new(Guid.NewGuid(), "history", "Online", "test", 1, 1, "Windows", "X64", "history-fixture", now, now, false);
        await store.AddRegistrationTokenAsync(Token("history-token", null, now, now.AddMinutes(5)), CancellationToken.None).ConfigureAwait(true);
        Assert.NotNull(await store.TryRegisterAsync("history-token", node, new(Guid.NewGuid(), node.Id, "history-credential", now, null, 1), now, CancellationToken.None).ConfigureAwait(true));
        Guid instanceId = Guid.NewGuid(), otherId = Guid.NewGuid();
        InstanceMetricSampleDto sample = new(now, 10, 100, null);
        DateTimeOffset cutoff = now.AddHours(-24);
        await store.AddInstanceMetricAsync(new(node.Id, instanceId, sample with { SampledAt = cutoff.AddSeconds(-1) }), cutoff, CancellationToken.None).ConfigureAwait(true);
        await store.AddInstanceMetricAsync(new(node.Id, instanceId, sample with { SampledAt = cutoff }), cutoff, CancellationToken.None).ConfigureAwait(true);
        await store.AddInstanceMetricAsync(new(node.Id, instanceId, sample with { SampledAt = now.AddSeconds(5).ToOffset(TimeSpan.FromHours(8)), CpuPercent = 90, MemoryBytes = 300 }), cutoff, CancellationToken.None).ConfigureAwait(true);
        await store.AddInstanceMetricAsync(new(node.Id, instanceId, sample), cutoff, CancellationToken.None).ConfigureAwait(true);
        await store.AddInstanceMetricAsync(new(node.Id, otherId, sample with { CpuPercent = 25 }), cutoff, CancellationToken.None).ConfigureAwait(true);
        IReadOnlyList<InstanceMetricSampleDto> history = await store.ListInstanceMetricsAsync(node.Id, instanceId, DateTimeOffset.MinValue, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(2, history.Count);
        Assert.Equal(cutoff, history[0].SampledAt);
        Assert.Equal(now.AddSeconds(5), history[1].SampledAt);
        Assert.Equal(90, history[1].CpuPercent);
        Assert.Equal(300, history[1].MemoryBytes);
        Assert.Null(history[1].OnlinePlayers);
        Assert.Equal(25, Assert.Single(await store.ListInstanceMetricsAsync(node.Id, otherId, cutoff, CancellationToken.None).ConfigureAwait(true)).CpuPercent);
        Assert.Empty(await store.ListInstanceMetricsAsync(Guid.NewGuid(), instanceId, cutoff, CancellationToken.None).ConfigureAwait(true));

        DateTimeOffset later = now.AddHours(25);
        await store.AddInstanceMetricAsync(new(node.Id, instanceId, sample with { SampledAt = later }), later.AddHours(-24), CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(later, Assert.Single(await store.ListInstanceMetricsAsync(node.Id, instanceId, DateTimeOffset.MinValue, CancellationToken.None).ConfigureAwait(true)).SampledAt);
        Assert.Empty(await store.ListInstanceMetricsAsync(node.Id, otherId, DateTimeOffset.MinValue, CancellationToken.None).ConfigureAwait(true));
        await store.RemoveNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(await store.ListInstanceMetricsAsync(node.Id, instanceId, DateTimeOffset.MinValue, CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    public async Task 节点元数据迁移保留旧配置且连接更新不覆盖配置_指标容量与保留边界一致()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-node-upgrade-{Guid.NewGuid():N}")).FullName;
        DbContextOptions<ServitiumDbContext> options = new DbContextOptionsBuilder<ServitiumDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "server.db") + ";Pooling=False").Options;
        TestContextFactory factory = new(options);
        Guid id = Guid.NewGuid();
        DateTimeOffset now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        UpdateNodeRequest configuration = new("Node A", @"D:\Synthetic\Managed", null, null, "preserve");
        using (ServitiumDbContext context = factory.CreateDbContext())
        {
            await context.GetService<IMigrator>().MigrateAsync("20260803203549_AddHmclReleaseActivations").ConfigureAwait(true);
            _ = await context.Database.ExecuteSqlAsync($"""
                INSERT INTO Nodes (Id, Name, Status, AgentVersion, ProtocolMajor, ProtocolMinor, OperatingSystem, Architecture, MachineFingerprint, CreatedAt, CredentialRevoked, ConfigurationJson)
                VALUES ({id.ToString("D")}, {"Node A"}, {"Offline"}, {"0.1"}, {1}, {0}, {"Windows"}, {"X64"}, {"upgrade-fixture"}, {now}, {false}, {JsonSerializer.Serialize(configuration)})
                """).ConfigureAwait(true);
            await context.Database.MigrateAsync().ConfigureAwait(true);
        }
        using SqliteWriteCoordinator coordinator = new();
        SqliteNodeControlStore store = new(factory, coordinator, new SystemClock());
        StoredNode before = (await store.FindNodeAsync(id, CancellationToken.None).ConfigureAwait(true))!;
        Assert.Null(before.HostName);
        Assert.Null(before.IsLocal);
        await store.UpdateConnectionDetailsAsync(before with { HostName = "synthetic-host", IsLocal = true, ProtocolMinor = 1 }, CancellationToken.None).ConfigureAwait(true);
        StoredNode after = (await store.FindNodeAsync(id, CancellationToken.None).ConfigureAwait(true))!;
        Assert.Equal("synthetic-host", after.HostName);
        Assert.True(after.IsLocal);
        Assert.Equal("Node A", after.Name);
        Assert.Equal(JsonSerializer.Serialize(configuration), JsonSerializer.Serialize(after.Configuration));

        NodeMetricsDto sample = new(now, 12, 100, 1000, 500, 0, 0, TimeSpan.FromHours(1), 2000);
        foreach (DateTimeOffset timestamp in new[] { now.AddHours(-25), now.AddHours(-24), now })
        {
            await store.AddMetricAsync(new(id, sample with { SampledAt = timestamp }), now.AddHours(-24), CancellationToken.None).ConfigureAwait(true);
        }
        IReadOnlyList<StoredNodeMetric> retained = await store.ListMetricsAsync(id, DateTimeOffset.MinValue, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(new[] { now.AddHours(-24), now }, retained.Select(value => value.Metrics.SampledAt));
        Assert.All(retained, value => Assert.Equal(2000, value.Metrics.DiskTotalBytes));
        Assert.Empty(await store.ListMetricsAsync(Guid.NewGuid(), DateTimeOffset.MinValue, CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-330")]
    [Trait("TestId", "INT-340")]
    [Trait("TestId", "INT-350")]
    public async Task 节点存储_原子消费令牌并支持心跳轮换与吊销()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-nodes-{Guid.NewGuid():N}")).FullName;
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

            SqliteNodeControlStore store = new(factory, coordinator, new SystemClock());
            DateTimeOffset now = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
            StoredNodeRegistrationToken validToken = Token("valid-hash", "Node A", now, now.AddMinutes(15));
            StoredNodeRegistrationToken mismatchedToken = Token("mismatch-hash", "Node B", now, now.AddMinutes(15));
            StoredNodeRegistrationToken expiredToken = Token("expired-hash", null, now.AddMinutes(-20), now.AddMinutes(-5));
            StoredNodeRegistrationToken revokableToken = Token("revoke-hash", null, now, now.AddMinutes(15));

            await store.AddRegistrationTokenAsync(validToken, CancellationToken.None).ConfigureAwait(true);
            await store.AddRegistrationTokenAsync(mismatchedToken, CancellationToken.None).ConfigureAwait(true);
            await store.AddRegistrationTokenAsync(expiredToken, CancellationToken.None).ConfigureAwait(true);
            await store.AddRegistrationTokenAsync(revokableToken, CancellationToken.None).ConfigureAwait(true);

            IReadOnlyList<StoredNodeRegistrationToken> initialTokens = await store.ListRegistrationTokensAsync(now, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(3, initialTokens.Count);
            Assert.DoesNotContain(initialTokens, token => token.Id == expiredToken.Id);

            await store.RevokeRegistrationTokenAsync(revokableToken.Id, CancellationToken.None).ConfigureAwait(true);
            IReadOnlyList<StoredNodeRegistrationToken> afterRevoke = await store.ListRegistrationTokensAsync(now, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(2, afterRevoke.Count);
            Assert.DoesNotContain(afterRevoke, token => token.Id == revokableToken.Id);

            StoredNode node = new(
                Guid.NewGuid(),
                "node a",
                "Registered",
                "1.0.0",
                1,
                0,
                "Windows",
                "X64",
                "synthetic-machine",
                now,
                null,
                false);
            StoredNodeCredential credential = new(Guid.NewGuid(), node.Id, "credential-v1", now, null, 1);

            Assert.Null(await store.TryRegisterAsync("missing-hash", node, credential, now, CancellationToken.None).ConfigureAwait(true));
            Assert.Null(await store.TryRegisterAsync("expired-hash", node, credential, now, CancellationToken.None).ConfigureAwait(true));
            Assert.Null(await store.TryRegisterAsync("mismatch-hash", node, credential, now, CancellationToken.None).ConfigureAwait(true));
            StoredNode? registered = await store.TryRegisterAsync("valid-hash", node, credential, now, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(node, registered);
            Assert.Null(await store.TryRegisterAsync("valid-hash", node, credential, now, CancellationToken.None).ConfigureAwait(true));

            _ = Assert.Single(await store.ListRegistrationTokensAsync(now, CancellationToken.None).ConfigureAwait(true));
            Assert.Null(await store.AuthenticateCredentialAsync("unknown", CancellationToken.None).ConfigureAwait(true));
            Assert.Equal(node.Id, (await store.AuthenticateCredentialAsync("credential-v1", CancellationToken.None).ConfigureAwait(true))!.Id);
            Assert.Equal(node.Id, (await store.FindNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true))!.Id);
            Assert.Null(await store.FindNodeAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(true));
            Assert.Equal(node.Id, Assert.Single(await store.ListNodesAsync(CancellationToken.None).ConfigureAwait(true)).Id);

            DateTimeOffset heartbeat = now.AddMinutes(1);
            await store.UpdateHeartbeatAsync(node.Id, "Online", "1.1.0", heartbeat, CancellationToken.None).ConfigureAwait(true);
            StoredNode updated = (await store.FindNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true))!;
            Assert.Equal("Online", updated.Status);
            Assert.Equal("1.1.0", updated.AgentVersion);
            Assert.Equal(heartbeat, updated.LastSeenAt);

            StoredNodeCredential replacement = new(Guid.NewGuid(), node.Id, "credential-v2", now.AddMinutes(2), null, 2);
            await store.RotateCredentialAsync(node.Id, replacement, CancellationToken.None).ConfigureAwait(true);
            Assert.Null(await store.AuthenticateCredentialAsync("credential-v1", CancellationToken.None).ConfigureAwait(true));
            Assert.Equal(node.Id, (await store.AuthenticateCredentialAsync("credential-v2", CancellationToken.None).ConfigureAwait(true))!.Id);

            await store.RevokeCredentialsAsync(node.Id, now.AddMinutes(3), CancellationToken.None).ConfigureAwait(true);
            Assert.Null(await store.AuthenticateCredentialAsync("credential-v2", CancellationToken.None).ConfigureAwait(true));
            StoredNode revoked = (await store.FindNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true))!;
            Assert.True(revoked.CredentialRevoked);
            Assert.Equal("Revoked", revoked.Status);
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

    private static StoredNodeRegistrationToken Token(string hash, string? expectedName, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        return new StoredNodeRegistrationToken(Guid.NewGuid(), hash, expectedName, createdAt, expiresAt, null, false);
    }

    private sealed class TestContextFactory(DbContextOptions<ServitiumDbContext> options) : IDbContextFactory<ServitiumDbContext>
    {
        public ServitiumDbContext CreateDbContext()
        {
            return new ServitiumDbContext(options);
        }
    }
}

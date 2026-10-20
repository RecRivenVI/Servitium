using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.ControlPlane;
using Servitium.Agent.Hosting;
using Servitium.Agent.Security;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class AgentLocalPersistenceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-680")]
    public async Task Outbox_重连前保持事件并仅在确认后删除()
    {
        OutboxContext test = await OutboxContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        await test.Store.EnqueueAsync("instance.status-changed", "instance:" + Guid.NewGuid().ToString("D"), new { status = "Running" }, true, CancellationToken.None).ConfigureAwait(true);
        await test.Store.EnqueueAsync("instance.resource-usage", "instance:" + Guid.NewGuid().ToString("D"), new { cpuPercent = 1.25 }, false, CancellationToken.None).ConfigureAwait(true);

        IReadOnlyList<AgentOutboxItem> beforeReconnect = await test.Store.ReadAsync(100, CancellationToken.None).ConfigureAwait(true);
        AgentOutboxStore reopened = test.CreateStore();
        IReadOnlyList<AgentOutboxItem> afterReconnect = await reopened.ReadAsync(100, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, beforeReconnect.Count);
        Assert.Equal(beforeReconnect, afterReconnect);
        Task acknowledgement = reopened.WaitForAcknowledgementAsync(afterReconnect[0].Sequence, CancellationToken.None);
        await reopened.AcknowledgeAsync(afterReconnect[0].Sequence, CancellationToken.None).ConfigureAwait(true);
        await acknowledgement.ConfigureAwait(true);
        AgentOutboxItem remaining = Assert.Single(await reopened.ReadAsync(100, CancellationToken.None).ConfigureAwait(true));
        Assert.Equal(afterReconnect[1].Sequence, remaining.Sequence);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-680")]
    public async Task Outbox_连接取消后可由下一连接等待同一确认()
    {
        OutboxContext test = await OutboxContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        await test.Store.EnqueueAsync("instance.status-changed", "instance:" + Guid.NewGuid().ToString("D"), new { status = "Stopped" }, true, CancellationToken.None).ConfigureAwait(true);
        AgentOutboxItem item = Assert.Single(await test.Store.ReadAsync(1, CancellationToken.None).ConfigureAwait(true));
        using (CancellationTokenSource cancelled = new())
        {
            await cancelled.CancelAsync().ConfigureAwait(true);
            _ = await Assert.ThrowsAsync<TaskCanceledException>(() =>
                test.Store.WaitForAcknowledgementAsync(item.Sequence, cancelled.Token)).ConfigureAwait(true);
        }

        Task nextConnection = test.Store.WaitForAcknowledgementAsync(item.Sequence, CancellationToken.None);
        await test.Store.AcknowledgeAsync(item.Sequence, CancellationToken.None).ConfigureAwait(true);
        await nextConnection.ConfigureAwait(true);
        Assert.Empty(await test.Store.ReadAsync(1, CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-210")]
    public async Task 运行凭据_加密落盘且不会从列表泄露密码()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-credentials-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using RunCredentialStore store = new(new AgentHostOptions { DataRoot = root }, new XorProtector());
            await store.AddOrReplaceAsync("minecraft-main", "fixture-user", "FIXTURE", "synthetic-password", CancellationToken.None).ConfigureAwait(true);

            RunCredentialSummary summary = Assert.Single(await store.ListAsync(CancellationToken.None).ConfigureAwait(true));
            Assert.Equal("minecraft-main", summary.Alias);
            Assert.Equal("fixture-user", summary.UserName);
            Assert.DoesNotContain("Password", summary.ToString(), StringComparison.OrdinalIgnoreCase);
            RunCredential resolved = await store.ResolveAsync("MINECRAFT-MAIN", CancellationToken.None).ConfigureAwait(true);
            Assert.Equal("synthetic-password", resolved.Password);

            string path = Path.Combine(root, "config", "run-credentials.dat");
            string stored = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path).ConfigureAwait(true));
            Assert.DoesNotContain("synthetic-password", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-user", stored, StringComparison.Ordinal);

            await store.DeleteAsync("minecraft-main", CancellationToken.None).ConfigureAwait(true);
            _ = await Assert.ThrowsAsync<DomainException>(() => store.ResolveAsync("minecraft-main", CancellationToken.None)).ConfigureAwait(true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    [InlineData("contains:semicolon")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-210")]
    public async Task 运行凭据_拒绝不安全别名(string alias)
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-credentials-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using RunCredentialStore store = new(new AgentHostOptions { DataRoot = root }, new XorProtector());
            DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
                store.AddOrReplaceAsync(alias, "fixture-user", null, "synthetic-password", CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("validation-failed", exception.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class OutboxContext : IAsyncDisposable
    {
        private readonly string _root;
        private readonly TestContextFactory _factory;
        private readonly SqliteWriteCoordinator _coordinator;

        private OutboxContext(string root, TestContextFactory factory, SqliteWriteCoordinator coordinator)
        {
            _root = root;
            _factory = factory;
            _coordinator = coordinator;
            Store = CreateStore();
        }

        internal AgentOutboxStore Store { get; }

        internal static async Task<OutboxContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-outbox-" + Guid.NewGuid().ToString("N"))).FullName;
            DbContextOptions<AgentDbContext> options = new DbContextOptionsBuilder<AgentDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "agent.db") + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            using (AgentDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            return new(root, factory, new SqliteWriteCoordinator());
        }

        internal AgentOutboxStore CreateStore()
        {
            return new(_factory, _coordinator, new FixedClock(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero)));
        }

        public ValueTask DisposeAsync()
        {
            _coordinator.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestContextFactory(DbContextOptions<AgentDbContext> options) : IDbContextFactory<AgentDbContext>
    {
        public AgentDbContext CreateDbContext()
        {
            return new(options);
        }
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow => value;
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            return Transform(plaintext);
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
        {
            return Transform(protectedData);
        }

        private static byte[] Transform(ReadOnlySpan<byte> source)
        {
            byte[] result = source.ToArray();
            for (int index = 0; index < result.Length; index++)
            {
                result[index] ^= 0xa5;
            }

            return result;
        }
    }
}

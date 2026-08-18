using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Nodes;
using Servitium.Infrastructure.Nodes;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class NodeControlStoreTests
{
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

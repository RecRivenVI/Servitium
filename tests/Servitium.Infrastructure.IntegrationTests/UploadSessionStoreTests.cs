using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Files;
using Servitium.Infrastructure.Files;
using Servitium.Infrastructure.Persistence;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class UploadSessionStoreTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-820")]
    [Trait("TestId", "REG-006")]
    public async Task 上传会话存储_SQLite可筛选过期时间并在重开后恢复分块()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-upload-store-{Guid.NewGuid():N}")).FullName;
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

            SqliteUploadSessionStore store = new(factory, coordinator);
            DateTimeOffset now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
            StoredUploadSession expired = Session(now.AddHours(-25), now.AddHours(-1));
            StoredUploadSession active = Session(now, now.AddHours(24));
            await store.AddAsync(expired, CancellationToken.None).ConfigureAwait(true);
            await store.AddAsync(active, CancellationToken.None).ConfigureAwait(true);
            StoredUploadSession received = await store.MarkPartReceivedAsync(active.Id, 0, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal([0], received.ReceivedParts);

            IReadOnlyList<StoredUploadSession> expiredSessions = await store.ListExpiredAsync(now, CancellationToken.None).ConfigureAwait(true);
            StoredUploadSession listed = Assert.Single(expiredSessions);
            Assert.Equal(expired.Id, listed.Id);

            SqliteUploadSessionStore reopened = new(factory, coordinator);
            StoredUploadSession restored = (await reopened.FindAsync(active.Id, CancellationToken.None).ConfigureAwait(true))!;
            Assert.Equal([0], restored.ReceivedParts);
            Assert.Equal(active.ExpiresAt, restored.ExpiresAt);
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

    private static StoredUploadSession Session(DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        return new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "synthetic.bin",
        "uploads/synthetic.bin",
        1,
        256 * 1024,
        1,
        [],
        false,
        createdAt,
        expiresAt);
    }

    private sealed class TestContextFactory(DbContextOptions<ServitiumDbContext> options) : IDbContextFactory<ServitiumDbContext>
    {
        public ServitiumDbContext CreateDbContext()
        {
            return new(options);
        }
    }
}

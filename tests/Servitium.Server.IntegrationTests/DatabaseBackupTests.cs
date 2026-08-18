using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Settings;
using Servitium.Domain.Auditing;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;
using Servitium.Server.Maintenance;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class DatabaseBackupTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1190")]
    public async Task SQLite在线备份可读取_每日去重并按数量保留()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"servitium-db-backup-{Guid.NewGuid():N}")).FullName;
        try
        {
            string databasePath = Path.Combine(root, "server.db");
            SqliteConnection database = new("Data Source=" + databasePath + ";Pooling=False");
            await using (database.ConfigureAwait(false))
            {
                await database.OpenAsync().ConfigureAwait(true);
                SqliteCommand command = database.CreateCommand();
                command.CommandText = "CREATE TABLE Synthetic(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL); INSERT INTO Synthetic(Value) VALUES ('safe');";
                _ = await command.ExecuteNonQueryAsync().ConfigureAwait(true);
            }

            MutableClock clock = new(new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero));
            CapturingActivityLogger activity = new();
            using DatabaseBackupService backups = new(new DatabaseLocation(databasePath), clock, activity, new GuidFactory());
            DatabaseBackupDto first = (await backups.CreateDailyIfDueAsync(2, CancellationToken.None).ConfigureAwait(true))!;
            Assert.Null(await backups.CreateDailyIfDueAsync(2, CancellationToken.None).ConfigureAwait(true));
            await AssertBackupAsync(Path.Combine(root, "backups", first.FileName)).ConfigureAwait(true);

            clock.UtcNow = clock.UtcNow.AddDays(1);
            DatabaseBackupDto second = (await backups.CreateDailyIfDueAsync(2, CancellationToken.None).ConfigureAwait(true))!;
            clock.UtcNow = clock.UtcNow.AddDays(1);
            DatabaseBackupDto third = (await backups.CreateDailyIfDueAsync(2, CancellationToken.None).ConfigureAwait(true))!;
            Assert.False(File.Exists(Path.Combine(root, "backups", first.FileName)));
            Assert.True(File.Exists(Path.Combine(root, "backups", second.FileName)));
            Assert.True(File.Exists(Path.Combine(root, "backups", third.FileName)));

            DatabaseBackupDto manual = await backups.CreateManualAsync(CancellationToken.None).ConfigureAwait(true);
            await AssertBackupAsync(Path.Combine(root, "backups", manual.FileName)).ConfigureAwait(true);
            Assert.Equal(3, activity.Entries.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task AssertBackupAsync(string path)
    {
        Assert.True(File.Exists(path));
        SqliteConnection connection = new("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM Synthetic LIMIT 1;";
            Assert.Equal("safe", Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(true), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class MutableClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }

    private sealed class CapturingActivityLogger : IActivityLogger
    {
        internal List<ActivityLogEntry> Entries { get; } = [];

        public Task AppendAsync(ActivityLogEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}

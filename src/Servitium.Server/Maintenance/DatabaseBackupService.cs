using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Settings;
using Servitium.Domain.Auditing;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Server.Maintenance;

/// <summary>通过 SQLite 在线备份 API 创建可热备份的数据库副本。</summary>
internal sealed class DatabaseBackupService(
    DatabaseLocation database,
    IClock clock,
    IActivityLogger activity,
    IGuidFactory guids) : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    internal async Task<DatabaseBackupDto> CreateManualAsync(CancellationToken cancellationToken)
    {
        return await CreateAsync("manual", cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DatabaseBackupDto?> CreateDailyIfDueAsync(int retentionCount, CancellationToken cancellationToken)
    {
        if (retentionCount is < 1 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionCount));
        }

        DateTimeOffset now = clock.UtcNow;
        string directory = BackupDirectory();
        _ = Directory.CreateDirectory(directory);
        string prefix = "server-daily-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        if (Directory.EnumerateFiles(directory, prefix + "*.db", SearchOption.TopDirectoryOnly).Any())
        {
            return null;
        }

        DatabaseBackupDto created = await CreateAsync("daily", cancellationToken).ConfigureAwait(false);
        string[] daily = [.. Directory.EnumerateFiles(directory, "server-daily-*.db", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)];
        foreach (string expired in daily.Skip(retentionCount))
        {
            File.Delete(expired);
        }

        await activity.AppendAsync(
            new ActivityLogEntry(
                guids.Create(),
                null,
                "system",
                "maintenance.database-backup",
                "database",
                now,
                new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fileName"] = created.FileName,
                    ["sizeBytes"] = created.SizeBytes.ToString(CultureInfo.InvariantCulture),
                }),
            cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async Task<DatabaseBackupDto> CreateAsync(string kind, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = clock.UtcNow;
            string directory = BackupDirectory();
            _ = Directory.CreateDirectory(directory);
            string fileName = "server-" + kind + "-" + now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + ".db";
            string finalPath = Path.Combine(directory, fileName);
            string temporaryPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                SqliteConnection source = new("Data Source=" + database.Path + ";Mode=ReadOnly;Pooling=False");
                await using (source.ConfigureAwait(false))
                {
                    SqliteConnection destination = new("Data Source=" + temporaryPath + ";Mode=ReadWriteCreate;Pooling=False");
                    await using (destination.ConfigureAwait(false))
                    {
                        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                        source.BackupDatabase(destination);
                        SqliteCommand integrity = destination.CreateCommand();
                        integrity.CommandText = "PRAGMA integrity_check;";
                        string? result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("The SQLite backup failed its integrity check.");
                        }
                    }
                }

                File.Move(temporaryPath, finalPath, overwrite: false);
                return new DatabaseBackupDto(fileName, new FileInfo(finalPath).Length, now);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _ = _mutex.Release();
        }
    }

    private string BackupDirectory()
    {
        return Path.Combine(Path.GetDirectoryName(database.Path)!, "backups");
    }

    public void Dispose()
    {
        _mutex.Dispose();
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Servitium.Infrastructure.Persistence;

/// <summary>数据库文件位置。</summary>
/// <param name="Path">数据库绝对路径。</param>
public sealed record DatabaseLocation(string Path);

/// <summary>在宿主接收请求前备份并迁移 SQLite 数据库。</summary>
public sealed partial class DatabaseMigrationHostedService(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    DatabaseLocation location,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(location.Path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The database path must have a parent directory.");
        }

        _ = Directory.CreateDirectory(directory);
        string[] pending;
        using (ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            pending = [.. await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)];
        }

        string? backupPath = null;
        if (pending.Length > 0 && File.Exists(location.Path))
        {
            string backupDirectory = Path.Combine(directory, "backups");
            _ = Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(
                backupDirectory,
                "server-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + ".db");
            File.Copy(location.Path, backupPath, false);
        }

        try
        {
            using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            _ = await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            _ = await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
            _ = await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            _ = await context.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
            MigrationCompleted(logger, pending.Length, location.Path, backupPath ?? string.Empty);
        }
        catch
        {
            if (backupPath is not null)
            {
                MigrationFailedWithBackup(logger, backupPath);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    [LoggerMessage(1001, LogLevel.Information, "Applied {MigrationCount} database migrations to {DatabasePath}; backup: {BackupPath}")]
    private static partial void MigrationCompleted(ILogger logger, int migrationCount, string databasePath, string backupPath);

    [LoggerMessage(1002, LogLevel.Error, "Database migration failed; the pre-migration backup is retained at {BackupPath}")]
    private static partial void MigrationFailedWithBackup(ILogger logger, string backupPath);
}

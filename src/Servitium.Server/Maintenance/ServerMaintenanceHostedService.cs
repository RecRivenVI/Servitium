using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Servitium.Contracts.Settings;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Events;
using Servitium.Application.Abstractions;
using Servitium.Infrastructure.Distribution;
using Servitium.Infrastructure.Maintenance;
using Servitium.Infrastructure.Persistence;
using Servitium.Server.Hosting;
using Servitium.Server.Hmcl;
using Servitium.Server.Realtime;

namespace Servitium.Server.Maintenance;

/// <summary>定期执行控制平面保留期清理、日志回收和每日数据库备份。</summary>
internal sealed partial class ServerMaintenanceHostedService(
    RetentionMaintenanceService retention,
    DatabaseBackupService backups,
    HmclDistributionStore hmcl,
    HmclArchiveUploadManager hmclArchives,
    ControlPlaneStore settings,
    ServerHostOptions options,
    IClock clock,
    IRealtimeEventStream events,
    ILogger<ServerMaintenanceHostedService> logger) : BackgroundService
{
    private DateOnly? _lastHmclIntegrityDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceSafelyAsync(stoppingToken).ConfigureAwait(false);
        using PeriodicTimer timer = new(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunOnceSafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "后台维护循环必须记录单次故障并在下一周期继续。")]
    private async Task RunOnceSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<SettingsGroupDto> groups = await settings.ListSettingsAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string?> values = groups.SingleOrDefault(group => group.Group == "general")?.Values
                ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            RetentionPolicy policy = new(
                Int(values, "activityRetentionDays", 180),
                Int(values, "auditRetentionDays", 365),
                Int(values, "operationRetentionDays", 30),
                Int(values, "metricRetentionHours", 24));
            _ = await retention.RunAsync(policy, cancellationToken).ConfigureAwait(false);
            _ = await hmcl.ReclaimAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await hmclArchives.CleanupExpiredAsync(cancellationToken).ConfigureAwait(false);
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            if (_lastHmclIntegrityDate != today)
            {
                HmclIntegrityResultDto integrity = await hmcl.InspectIntegrityAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
                foreach (HmclIntegrityFailureDto failure in integrity.Failures)
                {
                    _ = await events.PublishAsync(
                        EventTypes.HmclIntegrityAlert,
                        "global",
                        new HmclIntegrityAlertPayload(failure.ChannelId, failure.ReleaseId, failure.Paths.Count),
                        cancellationToken).ConfigureAwait(false);
                }

                _lastHmclIntegrityDate = today;
            }

            CleanupServiceLogs(Int(values, "serviceLogRetentionDays", 30));
            CleanupExports();

            bool backupEnabled = !values.TryGetValue("databaseBackupEnabled", out string? enabled) || !bool.TryParse(enabled, out bool parsed) || parsed;
            if (backupEnabled)
            {
                _ = await backups.CreateDailyIfDueAsync(Int(values, "databaseBackupRetentionCount", 7), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MaintenanceFailed(logger, exception);
        }
    }

    private void CleanupServiceLogs(int retentionDays)
    {
        if (retentionDays is < 7 or > 365)
        {
            throw new InvalidDataException("The service log retention is outside its allowed range.");
        }

        string directory = Path.GetFullPath(Path.Combine(options.DataRoot, "logs"));
        if (!Directory.Exists(directory))
        {
            return;
        }

        DateTime cutoff = clock.UtcNow.UtcDateTime.AddDays(-retentionDays);
        foreach (string path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
            {
                File.Delete(path);
            }
        }
    }

    private void CleanupExports()
    {
        string directory = Path.GetFullPath(Path.Combine(options.DataRoot, "exports"));
        if (!Directory.Exists(directory))
        {
            return;
        }

        DateTime cutoff = clock.UtcNow.UtcDateTime.AddDays(-7);
        foreach (string path in Directory.EnumerateFiles(directory, "diagnostics-*.zip", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
            {
                File.Delete(path);
            }
        }
    }

    private static int Int(IReadOnlyDictionary<string, string?> values, string key, int fallback)
    {
        return values.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    [LoggerMessage(1601, LogLevel.Error, "Server maintenance cycle failed")]
    private static partial void MaintenanceFailed(ILogger logger, Exception exception);
}

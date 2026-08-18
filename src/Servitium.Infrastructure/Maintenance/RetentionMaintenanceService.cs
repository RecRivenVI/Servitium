using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Abstractions;
using Servitium.Domain.Auditing;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Infrastructure.Maintenance;

/// <summary>控制平面数据保留策略。</summary>
public sealed record RetentionPolicy(
    int ActivityDays = 180,
    int AuditDays = 365,
    int OperationDays = 30,
    int MetricHours = 24);

/// <summary>一次保留期清理的删除计数。</summary>
public sealed record RetentionCleanupResult(
    int Activities,
    int Audits,
    int Operations,
    int Metrics,
    int ExpiredSecurityState);

/// <summary>执行数据库内保留期清理，并为作业自身追加操作和审计记录。</summary>
public sealed class RetentionMaintenanceService(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    IActivityLogger activityLogger,
    IAuditLogger auditLogger,
    IClock clock,
    IGuidFactory guids)
{
    /// <summary>执行一次清理。</summary>
    public async Task<RetentionCleanupResult> RunAsync(RetentionPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Validate(policy);
        DateTimeOffset now = clock.UtcNow;
        RetentionCleanupResult result = await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                DateTimeOffset activityCutoff = now.AddDays(-policy.ActivityDays);
                DateTimeOffset auditCutoff = now.AddDays(-policy.AuditDays);
                DateTimeOffset operationCutoff = now.AddDays(-policy.OperationDays);
                DateTimeOffset metricCutoff = now.AddHours(-policy.MetricHours);
                int activities = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM ActivityLogs WHERE OccurredAt < {activityCutoff}", cancellationToken).ConfigureAwait(false);
                int audits = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM AuditLogs WHERE OccurredAt < {auditCutoff}", cancellationToken).ConfigureAwait(false);
                int operations = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM Operations WHERE CompletedAt IS NOT NULL AND CompletedAt < {operationCutoff}", cancellationToken).ConfigureAwait(false);
                int metrics = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM NodeMetrics WHERE SampledAt < {metricCutoff}", cancellationToken).ConfigureAwait(false);
                int expired = 0;
                expired += await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM IpBans WHERE ExpiresAt <= {now}", cancellationToken).ConfigureAwait(false);
                expired += await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM IdempotencyRecords WHERE ExpiresAt <= {now}", cancellationToken).ConfigureAwait(false);
                expired += await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM OidcStates WHERE ExpiresAt <= {now}", cancellationToken).ConfigureAwait(false);
                expired += await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM LocalSessionTickets WHERE ExpiresAt <= {now}", cancellationToken).ConfigureAwait(false);
                return new RetentionCleanupResult(activities, audits, operations, metrics, expired);
            },
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> properties = new(StringComparer.Ordinal)
        {
            ["activities"] = result.Activities.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["audits"] = result.Audits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["operations"] = result.Operations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["metrics"] = result.Metrics.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["expiredSecurityState"] = result.ExpiredSecurityState.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await activityLogger.AppendAsync(
            new ActivityLogEntry(guids.Create(), null, "system", "maintenance.retention", "system", now, properties),
            cancellationToken).ConfigureAwait(false);
        await auditLogger.AppendAsync(
            new AuditLogEntry(guids.Create(), null, "system", AuditCategory.DestructiveAction, "maintenance.audit-retention", "audit", now, "ok"),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static void Validate(RetentionPolicy policy)
    {
        if (policy.ActivityDays is < 30 or > 3650 || policy.AuditDays is < 90 or > 3650 ||
            policy.OperationDays is < 7 or > 365 || policy.MetricHours is < 1 or > 168)
        {
            throw new DomainException("validation-failed", "The retention policy is outside its allowed range.");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Settings;
using Servitium.Domain.Primitives;
using Servitium.Domain.Operations;

namespace Servitium.Infrastructure.Persistence;

/// <summary>长操作、设置和临时封禁的 SQLite 生产存储。</summary>
public sealed class ControlPlaneStore(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    SqliteWriteCoordinator writeQueue,
    IClock clock,
    IGuidFactory guidFactory) : IOperationTracker, IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.Operations.Where(row => row.Status == "Pending" || row.Status == "Running")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.Status, "Failed")
                        .SetProperty(row => row.ErrorCode, "operation-interrupted")
                        .SetProperty(row => row.CompletedAt, clock.UtcNow)
                        .SetProperty(row => row.Cancellable, false), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Operation> CreateAsync(OperationKind kind, string target, CancellationToken cancellationToken)
    {
        return await CreateCoreAsync(kind, target, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Operation> CreateAsync(OperationKind kind, string target, Guid initiatedBy, CancellationToken cancellationToken)
    {
        return await CreateCoreAsync(kind, target, initiatedBy, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Operation> CreateCoreAsync(OperationKind kind, string target, Guid? initiatedBy, CancellationToken cancellationToken)
    {
        Operation operation = new(guidFactory.Create(), kind, target, clock.UtcNow);
        operation.Start();
        await writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.Operations.Add(new OperationRow
                {
                    Id = operation.Id,
                    Kind = operation.Kind.ToString(),
                    Target = operation.Target,
                    Status = operation.Status.ToString(),
                    Progress = 0,
                    CreatedAt = operation.CreatedAt,
                    CreatedAtUnixMilliseconds = operation.CreatedAt.ToUnixTimeMilliseconds(),
                    InitiatedBy = initiatedBy,
                    Cancellable = true,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return operation;
    }

    /// <summary>取消指定用户尚未到达提交点的所有操作。</summary>
    public Task<int> CancelRunningByUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                return await context.Operations
                    .Where(row => row.InitiatedBy == userId && row.Cancellable && (row.Status == "Pending" || row.Status == "Running"))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.Status, "Cancelled")
                        .SetProperty(row => row.CompletedAt, clock.UtcNow)
                        .SetProperty(row => row.Cancellable, false), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ReportProgressAsync(Guid operationId, double progress, string stage, CancellationToken cancellationToken)
    {
        return progress is < 0 or > 1 || string.IsNullOrWhiteSpace(stage) || stage.Length > 128
            ? throw new DomainException("validation-failed", "The operation progress is invalid.")
            : UpdateAsync(operationId, row =>
        {
            EnsureRunning(row);
            row.Progress = progress;
            row.Stage = stage;
        }, cancellationToken);
    }

    /// <summary>完成操作。</summary>
    public Task CompleteAsync(Guid operationId, string? result, CancellationToken cancellationToken)
    {
        return UpdateAsync(operationId, row =>
        {
            EnsureRunning(row);
            row.Status = "Succeeded";
            row.Progress = 1;
            row.Result = result;
            row.CompletedAt = clock.UtcNow;
            row.Cancellable = false;
        }, cancellationToken);
    }

    /// <summary>标记操作失败。</summary>
    public Task FailAsync(Guid operationId, string errorCode, CancellationToken cancellationToken)
    {
        return FailAsync(operationId, errorCode, null, cancellationToken);
    }

    /// <summary>标记操作失败并保留可安全展示的结构化详情。</summary>
    public Task FailAsync(Guid operationId, string errorCode, string? result, CancellationToken cancellationToken)
    {
        return UpdateAsync(operationId, row =>
        {
            EnsureRunning(row);
            row.Status = "Failed";
            row.ErrorCode = errorCode;
            row.Result = result;
            row.CompletedAt = clock.UtcNow;
            row.Cancellable = false;
        }, cancellationToken);
    }

    /// <summary>取消尚未到达提交点的操作。</summary>
    public Task<bool> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                OperationRow? row = await context.Operations.SingleOrDefaultAsync(candidate => candidate.Id == operationId, cancellationToken).ConfigureAwait(false);
                if (row is null || !row.Cancellable || row.Status is not ("Pending" or "Running"))
                {
                    return false;
                }

                row.Status = "Cancelled";
                row.CompletedAt = clock.UtcNow;
                row.Cancellable = false;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>列出最近长操作。</summary>
    public async Task<IReadOnlyList<OperationDetailsDto>> ListOperationsAsync(CancellationToken cancellationToken)
    {
        return await ListOperationsAsync(null, null, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按协议过滤最近长操作；普通用户由调用方传入自己的发起者标识。</summary>
    public async Task<IReadOnlyList<OperationDetailsDto>> ListOperationsAsync(
        string? status,
        string? kind,
        string? target,
        Guid? initiatedBy,
        CancellationToken cancellationToken)
    {
        if ((status?.Length ?? 0) > 32 || (kind?.Length ?? 0) > 64 || (target?.Length ?? 0) > 256)
        {
            throw new DomainException("validation-failed", "The operation query is invalid.");
        }

        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<OperationRow> query = context.Operations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            string normalized = status.Trim();
            query = query.Where(row => row.Status == normalized);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            string normalized = kind.Trim();
            query = query.Where(row => row.Kind == normalized);
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            string normalized = target.Trim();
            query = query.Where(row => row.Target.Contains(normalized));
        }

        if (initiatedBy is Guid userId)
        {
            query = query.Where(row => row.InitiatedBy == userId);
        }

        OperationRow[] rows = await query.OrderByDescending(row => row.CreatedAtUnixMilliseconds).Take(200).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToDto)];
    }

    /// <summary>读取长操作详情。</summary>
    public async Task<OperationDetailsDto?> FindOperationAsync(Guid id, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        OperationRow? row = await context.Operations.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToDto(row);
    }

    /// <summary>读取全部设置组。</summary>
    public async Task<IReadOnlyList<SettingsGroupDto>> ListSettingsAsync(CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SettingRow[] rows = await context.Settings.AsNoTracking().OrderBy(row => row.Key).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToSettings)];
    }

    /// <summary>按乐观并发修订保存设置组。</summary>
    public Task<SettingsGroupDto> UpdateSettingsAsync(
        string group,
        IReadOnlyDictionary<string, string?> values,
        long expectedRevision,
        bool restartRequired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        return group is not ("general" or "appearance" or "security" or "identity") || values.Count > 128
            ? throw new DomainException("validation-failed", "The settings group is invalid.")
            : writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                SettingRow? row = await context.Settings.SingleOrDefaultAsync(candidate => candidate.Key == group, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    if (expectedRevision != 0)
                    {
                        throw new DomainException("conflict", "The settings group changed after it was read.");
                    }

                    row = new SettingRow { Key = group, Revision = 1 };
                    _ = context.Settings.Add(row);
                }
                else
                {
                    if (row.Revision != expectedRevision)
                    {
                        throw new DomainException("conflict", "The settings group changed after it was read.");
                    }

                    row.Revision++;
                }

                row.ValueJson = JsonSerializer.Serialize(values);
                row.UpdatedAt = clock.UtcNow;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new SettingsGroupDto(group, values, row.Revision, restartRequired);
            },
            cancellationToken);
    }

    /// <summary>读取用户保存的单页布局。</summary>
    public async Task<DashboardLayoutDto?> FindDashboardLayoutAsync(Guid userId, string page, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        DashboardLayoutRow? row = await context.DashboardLayouts.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.Page == page, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToDashboardLayout(row);
    }

    /// <summary>读取用户保存的全部布局。</summary>
    public async Task<IReadOnlyList<DashboardLayoutDto>> ListDashboardLayoutsAsync(Guid userId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        DashboardLayoutRow[] rows = await context.DashboardLayouts.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderBy(row => row.Page)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ToDashboardLayout)];
    }

    /// <summary>原子保存用户页面布局。</summary>
    public Task SaveDashboardLayoutAsync(Guid userId, DashboardLayoutDto layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                DashboardLayoutRow? row = await context.DashboardLayouts
                    .SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.Page == layout.Page, cancellationToken)
                    .ConfigureAwait(false);
                if (row is null)
                {
                    row = new DashboardLayoutRow { UserId = userId, Page = layout.Page };
                    _ = context.DashboardLayouts.Add(row);
                }

                row.Version = layout.Version;
                row.CardsJson = JsonSerializer.Serialize(layout.Cards);
                row.UpdatedAt = clock.UtcNow;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>删除用户的单页自定义布局，使其回退到默认值。</summary>
    public Task ResetDashboardLayoutAsync(Guid userId, string page, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.DashboardLayouts
                    .Where(row => row.UserId == userId && row.Page == page)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>列出尚未过期的 IP 封禁。</summary>
    public async Task<IReadOnlyList<IpBanDto>> ListIpBansAsync(CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IpBanRow[] rows = await context.IpBans.AsNoTracking().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(row => row.ExpiresAt > clock.UtcNow).OrderBy(row => row.ExpiresAt).Select(row => new IpBanDto(row.Address, row.ExpiresAt, row.ReasonCode))];
    }

    /// <summary>解除 IP 封禁。</summary>
    public Task RemoveIpBanAsync(string address, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.IpBans.Where(row => row.Address == address).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>读取指定地址尚未过期的封禁截止时间。</summary>
    public async Task<DateTimeOffset?> FindActiveIpBanAsync(string address, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IpBanRow? row = await context.IpBans.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Address == address, cancellationToken).ConfigureAwait(false);
        return row is not null && row.ExpiresAt > clock.UtcNow ? row.ExpiresAt : null;
    }

    /// <summary>创建或延长临时 IP 封禁。</summary>
    public Task UpsertIpBanAsync(string address, DateTimeOffset expiresAt, string reasonCode, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                IpBanRow? row = await context.IpBans.SingleOrDefaultAsync(candidate => candidate.Address == address, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    _ = context.IpBans.Add(new IpBanRow { Address = address, ExpiresAt = expiresAt, ReasonCode = reasonCode });
                }
                else if (expiresAt > row.ExpiresAt)
                {
                    row.ExpiresAt = expiresAt;
                    row.ReasonCode = reasonCode;
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private Task UpdateAsync(Guid id, Action<OperationRow> update, CancellationToken cancellationToken)
    {
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                OperationRow row = await context.Operations.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The operation does not exist.");
                update(row);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static void EnsureRunning(OperationRow row)
    {
        if (row.Status != "Running")
        {
            throw new DomainException("conflict", "The operation is no longer running.");
        }
    }

    private static OperationDetailsDto ToDto(OperationRow row)
    {
        OperationDto operation = new(row.Id, row.Kind, row.Status, row.Progress, row.Stage, row.ErrorCode, row.CreatedAt, row.CompletedAt);
        return new(operation, row.Target, row.InitiatedBy, row.Result, row.Cancellable);
    }

    private static SettingsGroupDto ToSettings(SettingRow row)
    {
        IReadOnlyDictionary<string, string?> values = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.ValueJson)
            ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        bool restartRequired = row.Key is "security" or "identity";
        return new(row.Key, values, row.Revision, restartRequired);
    }

    private static DashboardLayoutDto ToDashboardLayout(DashboardLayoutRow row)
    {
        IReadOnlyList<DashboardCardLayoutDto> cards = JsonSerializer.Deserialize<DashboardCardLayoutDto[]>(row.CardsJson) ?? [];
        return new DashboardLayoutDto(row.Page, row.Version, cards);
    }
}

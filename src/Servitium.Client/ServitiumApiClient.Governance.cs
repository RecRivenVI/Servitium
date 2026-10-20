using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client.Http;
using Servitium.Contracts.Auditing;
using Servitium.Contracts.Governance;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Settings;

namespace Servitium.Client;

/// <summary>操作日志筛选条件。</summary>
public sealed record ActivityQuery(
    string? Actor = null,
    string? Action = null,
    string? Resource = null,
    string? Outcome = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Cursor = null,
    int Limit = 100);

/// <summary>安全审计筛选条件。</summary>
public sealed record SecurityAuditQuery(
    string? Actor = null,
    string? Category = null,
    string? Action = null,
    string? Resource = null,
    string? Outcome = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Cursor = null,
    int Limit = 100);

public sealed partial class ServitiumApiClient
{
    /// <summary>读取治理仪表盘聚合。</summary>
    public Task<DashboardOverviewDto> GetDashboardAsync(CancellationToken cancellationToken)
    {
        return GetJsonAsync<DashboardOverviewDto>("api/v1/dashboard", cancellationToken);
    }

    /// <summary>列出长操作。</summary>
    public Task<CursorPage<OperationDetailsDto>> ListOperationsAsync(
        string? status,
        string? kind,
        string? target,
        Guid? initiatedBy,
        CancellationToken cancellationToken)
    {
        return ListOperationsAsync(status, kind, target, initiatedBy, null, 50, cancellationToken);
    }

    /// <summary>按状态、类型、目标和发起者筛选长操作，并使用游标分页。</summary>
    public Task<CursorPage<OperationDetailsDto>> ListOperationsAsync(
        string? status,
        string? kind,
        string? target,
        Guid? initiatedBy,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        return GetPageAsync<OperationDetailsDto>(
            $"api/v1/operations?status={Escape(status)}&kind={Escape(kind)}&target={Escape(target)}&{OptionalGuidQuery("initiatedBy", initiatedBy)}cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>读取长操作详情。</summary>
    public Task<OperationDetailsDto> GetOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<OperationDetailsDto>($"api/v1/operations/{operationId:D}", cancellationToken);
    }

    /// <summary>取消可取消的长操作。</summary>
    public Task CancelOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Post, $"api/v1/operations/{operationId:D}/cancel", cancellationToken);
    }

    /// <summary>查询操作日志。</summary>
    public Task<CursorPage<ActivityLogDto>> ListActivityLogsAsync(
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetPageAsync<ActivityLogDto>("api/v1/audit/activity?" + ActivityQueryText(query, includeCursor: true), cancellationToken);
    }

    /// <summary>把完整操作日志导出为防公式注入的 CSV。</summary>
    public Task<DownloadedFileInfo> ExportActivityLogsAsync(
        ActivityQuery query,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return DownloadToAsync(HttpMethod.Get, "api/v1/audit/activity/export?" + ActivityQueryText(query, includeCursor: false), destination, cancellationToken);
    }

    /// <summary>查询安全审计日志。</summary>
    public Task<CursorPage<AuditLogDto>> ListSecurityAuditAsync(
        SecurityAuditQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetPageAsync<AuditLogDto>("api/v1/audit/security?" + SecurityQueryText(query, includeCursor: true), cancellationToken);
    }

    /// <summary>把完整安全审计导出为防公式注入的 CSV。</summary>
    public Task<DownloadedFileInfo> ExportSecurityAuditAsync(
        SecurityAuditQuery query,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return DownloadToAsync(HttpMethod.Get, "api/v1/audit/security/export?" + SecurityQueryText(query, includeCursor: false), destination, cancellationToken);
    }

    /// <summary>读取全部系统设置组；秘密只会显示占位符。</summary>
    public Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return GetJsonAsync<SettingsDto>("api/v1/settings", cancellationToken);
    }

    /// <summary>以乐观并发修订更新设置组。</summary>
    public Task<SettingsGroupDto> UpdateSettingsAsync(
        string group,
        UpdateSettingsGroupRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpdateSettingsGroupRequest, SettingsGroupDto>(
            HttpMethod.Patch, "api/v1/settings/" + Escape(group), request, cancellationToken);
    }

    /// <summary>上传经过 Server 内容验证的外观资源。</summary>
    public async Task<SettingsAssetDto> UploadSettingsAssetAsync(
        string kind,
        Stream content,
        string fileName,
        string mediaType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        using MultipartFormDataContent form = new();
        using StreamContent file = new(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", Path.GetFileName(fileName));
        return await SendContentAsync<SettingsAssetDto>(
            HttpMethod.Post, "api/v1/settings/assets?kind=" + Escape(kind), form, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除外观资源。</summary>
    public Task DeleteSettingsAssetAsync(string kind, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, "api/v1/settings/assets/" + Escape(kind), cancellationToken);
    }

    /// <summary>通过受限出站客户端验证 OIDC 发现文档。</summary>
    public Task<SettingsTestResultDto> TestOidcAsync(
        TestOidcDiscoveryRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<TestOidcDiscoveryRequest, SettingsTestResultDto>(
            HttpMethod.Post, "api/v1/settings/identity/test", request, cancellationToken);
    }

    /// <summary>验证本机 PFX 证书。</summary>
    public Task<SettingsTestResultDto> TestTlsAsync(
        TestTlsCertificateRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<TestTlsCertificateRequest, SettingsTestResultDto>(
            HttpMethod.Post, "api/v1/settings/tls/test", request, cancellationToken);
    }

    /// <summary>读取 Server 系统信息并报告客户端时间。</summary>
    public Task<SystemInfoDto> GetSystemInfoAsync(DateTimeOffset clientTime, CancellationToken cancellationToken)
    {
        return GetJsonAsync<SystemInfoDto>("api/v1/settings/system-info?clientTime=" + Date(clientTime), cancellationToken);
    }

    /// <summary>流式生成并下载脱敏诊断包。</summary>
    public Task<DownloadedFileInfo> DownloadDiagnosticsAsync(Stream destination, CancellationToken cancellationToken)
    {
        return DownloadToAsync(HttpMethod.Post, "api/v1/settings/diagnostics", destination, cancellationToken);
    }

    /// <summary>创建 SQLite 在线备份。</summary>
    public Task<DatabaseBackupDto> BackupDatabaseAsync(CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<DatabaseBackupDto>(HttpMethod.Post, "api/v1/settings/database/backup", cancellationToken);
    }

    /// <summary>列出临时 IP 封禁。</summary>
    public Task<CursorPage<IpBanDto>> ListIpBansAsync(CancellationToken cancellationToken)
    {
        return ListIpBansAsync(null, 50, cancellationToken);
    }

    /// <summary>分页列出临时 IP 封禁。</summary>
    public Task<CursorPage<IpBanDto>> ListIpBansAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<IpBanDto>($"api/v1/ip-bans?cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
    }

    /// <summary>移除临时 IP 封禁。</summary>
    public Task RemoveIpBanAsync(string address, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, "api/v1/ip-bans/" + Escape(address), cancellationToken);
    }

    /// <summary>列出当前用户保存的页面布局。</summary>
    public Task<CursorPage<DashboardLayoutDto>> ListLayoutsAsync(CancellationToken cancellationToken)
    {
        return ListLayoutsAsync(null, 50, cancellationToken);
    }

    /// <summary>分页列出当前用户保存的页面布局。</summary>
    public Task<CursorPage<DashboardLayoutDto>> ListLayoutsAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<DashboardLayoutDto>($"api/v1/layouts?cursor={Escape(cursor)}&limit={limit.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
    }

    /// <summary>读取页面布局。</summary>
    public Task<DashboardLayoutDto> GetLayoutAsync(string page, CancellationToken cancellationToken)
    {
        return GetJsonAsync<DashboardLayoutDto>("api/v1/layouts/" + Escape(page), cancellationToken);
    }

    /// <summary>保存页面布局。</summary>
    public Task<DashboardLayoutDto> SaveLayoutAsync(
        string page,
        UpdateDashboardLayoutRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpdateDashboardLayoutRequest, DashboardLayoutDto>(
            HttpMethod.Put, "api/v1/layouts/" + Escape(page), request, cancellationToken);
    }

    /// <summary>恢复页面默认布局。</summary>
    public Task<DashboardLayoutDto> ResetLayoutAsync(string page, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<DashboardLayoutDto>(
            HttpMethod.Delete, "api/v1/layouts/" + Escape(page), cancellationToken);
    }

    private static string ActivityQueryText(ActivityQuery query, bool includeCursor)
    {
        return $"actor={Escape(query.Actor)}&action={Escape(query.Action)}&resource={Escape(query.Resource)}&outcome={Escape(query.Outcome)}" +
        $"&from={Date(query.From)}&to={Date(query.To)}" +
        (includeCursor ? $"&cursor={Escape(query.Cursor)}&limit={query.Limit.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
    }

    private static string SecurityQueryText(SecurityAuditQuery query, bool includeCursor)
    {
        return $"actor={Escape(query.Actor)}&category={Escape(query.Category)}&action={Escape(query.Action)}&resource={Escape(query.Resource)}&outcome={Escape(query.Outcome)}" +
        $"&from={Date(query.From)}&to={Date(query.To)}" +
        (includeCursor ? $"&cursor={Escape(query.Cursor)}&limit={query.Limit.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
    }
}

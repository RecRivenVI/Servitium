using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Transport;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Scheduling;

namespace Servitium.Client;

public sealed partial class ServitiumApiClient
{
    /// <summary>列出实例可管理的配置文件。</summary>
    public Task<CursorPage<ConfigurationDto>> ListConfigurationsAsync(
        Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return ListConfigurationsAsync(instanceId, nodeId, null, 50, cancellationToken);
    }

    /// <summary>分页列出实例可管理的配置文件。</summary>
    public Task<CursorPage<ConfigurationDto>> ListConfigurationsAsync(
        Guid instanceId, Guid nodeId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<ConfigurationDto>(
            ConfigurationPath(instanceId, nodeId) + "&cursor=" + Escape(cursor) + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>读取结构化配置。</summary>
    public Task<ConfigurationDetailsDto> GetConfigurationAsync(
        Guid instanceId, Guid nodeId, string configKey, CancellationToken cancellationToken)
    {
        return GetJsonAsync<ConfigurationDetailsDto>(ConfigurationPath(instanceId, nodeId, Escape(configKey)), cancellationToken);
    }

    /// <summary>保存配置并产生自动备份。</summary>
    public Task<ConfigurationDetailsDto> WriteConfigurationAsync(
        Guid instanceId,
        Guid nodeId,
        string configKey,
        WriteConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<WriteConfigurationRequest, ConfigurationDetailsDto>(
            HttpMethod.Put, ConfigurationPath(instanceId, nodeId, Escape(configKey)), request, cancellationToken);
    }

    /// <summary>读取内置配置字段架构。</summary>
    public Task<IReadOnlyList<ConfigurationFieldDto>> GetConfigurationSchemaAsync(
        Guid instanceId, Guid nodeId, string configKey, CancellationToken cancellationToken)
    {
        return GetJsonAsync<IReadOnlyList<ConfigurationFieldDto>>(
            ConfigurationPath(instanceId, nodeId, "schema/" + Escape(configKey)), cancellationToken);
    }

    /// <summary>列出配置备份。</summary>
    public Task<CursorPage<ConfigurationBackupDto>> ListConfigurationBackupsAsync(
        Guid instanceId, Guid nodeId, string configKey, CancellationToken cancellationToken)
    {
        return ListConfigurationBackupsAsync(instanceId, nodeId, configKey, null, 50, cancellationToken);
    }

    /// <summary>分页列出配置备份。</summary>
    public Task<CursorPage<ConfigurationBackupDto>> ListConfigurationBackupsAsync(
        Guid instanceId, Guid nodeId, string configKey, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<ConfigurationBackupDto>(
            ConfigurationPath(instanceId, nodeId, Escape(configKey) + "/backups") + "&cursor=" + Escape(cursor) + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>恢复配置备份。</summary>
    public Task<ConfigurationDetailsDto> RestoreConfigurationBackupAsync(
        Guid instanceId,
        Guid nodeId,
        string configKey,
        Guid backupId,
        CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<ConfigurationDetailsDto>(
            HttpMethod.Post,
            ConfigurationPath(instanceId, nodeId, $"{Escape(configKey)}/backups/{backupId:D}/restore"),
            cancellationToken);
    }

    /// <summary>列出模组或插件。</summary>
    public Task<CursorPage<ModDetailsDto>> ListModsAsync(
        Guid instanceId, Guid nodeId, string kind, CancellationToken cancellationToken)
    {
        return ListModsAsync(instanceId, nodeId, kind, null, null, 50, cancellationToken);
    }

    /// <summary>按名称、标识或作者筛选并分页列出模组或插件。</summary>
    public Task<CursorPage<ModDetailsDto>> ListModsAsync(
        Guid instanceId, Guid nodeId, string kind, string? search, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<ModDetailsDto>(
            ModPath(instanceId, nodeId) + "&kind=" + Escape(kind) + "&search=" + Escape(search) + "&cursor=" + Escape(cursor) + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>读取模组或插件元数据。</summary>
    public Task<ModDetailsDto> GetModAsync(
        Guid instanceId, Guid nodeId, string kind, string modId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<ModDetailsDto>(ModPath(instanceId, nodeId, Escape(modId)) + "&kind=" + Escape(kind), cancellationToken);
    }

    /// <summary>启用模组或插件。</summary>
    public Task<ModDetailsDto> EnableModAsync(
        Guid instanceId, Guid nodeId, string kind, string modId, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<ModDetailsDto>(
            HttpMethod.Post, ModPath(instanceId, nodeId, Escape(modId) + "/enable") + "&kind=" + Escape(kind), cancellationToken);
    }

    /// <summary>禁用模组或插件。</summary>
    public Task<ModDetailsDto> DisableModAsync(
        Guid instanceId, Guid nodeId, string kind, string modId, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<ModDetailsDto>(
            HttpMethod.Post, ModPath(instanceId, nodeId, Escape(modId) + "/disable") + "&kind=" + Escape(kind), cancellationToken);
    }

    /// <summary>删除模组或插件文件。</summary>
    public Task DeleteModAsync(
        Guid instanceId, Guid nodeId, string kind, string modId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(
            HttpMethod.Delete, ModPath(instanceId, nodeId, Escape(modId)) + "&kind=" + Escape(kind), cancellationToken);
    }

    /// <summary>列出模组相关配置文件。</summary>
    public Task<CursorPage<string>> ListModConfigurationFilesAsync(
        Guid instanceId, Guid nodeId, string kind, string modId, CancellationToken cancellationToken)
    {
        return ListModConfigurationFilesAsync(instanceId, nodeId, kind, modId, null, 50, cancellationToken);
    }

    /// <summary>分页列出模组相关配置文件。</summary>
    public Task<CursorPage<string>> ListModConfigurationFilesAsync(
        Guid instanceId, Guid nodeId, string kind, string modId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<string>(
            ModPath(instanceId, nodeId, Escape(modId) + "/config-files") + "&kind=" + Escape(kind) +
            "&cursor=" + Escape(cursor) + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>把已完成的分块上传登记为模组或插件。</summary>
    public Task<OperationAcceptedDto> CompleteModUploadAsync(
        Guid instanceId,
        Guid nodeId,
        UploadModRequest request,
        CancellationToken cancellationToken)
    {
        return QueueJsonAsync(HttpMethod.Post, ModPath(instanceId, nodeId, "upload"), request, cancellationToken);
    }

    /// <summary>列出计划任务。</summary>
    public Task<CursorPage<ScheduledTaskDetailsDto>> ListSchedulesAsync(
        Guid instanceId, CancellationToken cancellationToken)
    {
        return ListSchedulesAsync(instanceId, null, 50, cancellationToken);
    }

    /// <summary>分页列出计划任务。</summary>
    public Task<CursorPage<ScheduledTaskDetailsDto>> ListSchedulesAsync(
        Guid instanceId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<ScheduledTaskDetailsDto>(
            SchedulePath(instanceId) + "?cursor=" + Escape(cursor) + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    /// <summary>创建计划任务。</summary>
    public Task<ScheduledTaskDetailsDto> CreateScheduleAsync(
        Guid instanceId, UpsertScheduledTaskRequest request, CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpsertScheduledTaskRequest, ScheduledTaskDetailsDto>(
            HttpMethod.Post, SchedulePath(instanceId), request, cancellationToken);
    }

    /// <summary>读取计划任务。</summary>
    public Task<ScheduledTaskDetailsDto> GetScheduleAsync(
        Guid instanceId, Guid scheduleId, CancellationToken cancellationToken)
    {
        return GetJsonAsync<ScheduledTaskDetailsDto>(SchedulePath(instanceId, scheduleId.ToString("D")), cancellationToken);
    }

    /// <summary>替换计划任务。</summary>
    public Task<ScheduledTaskDetailsDto> UpdateScheduleAsync(
        Guid instanceId,
        Guid scheduleId,
        UpsertScheduledTaskRequest request,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync<UpsertScheduledTaskRequest, ScheduledTaskDetailsDto>(
            HttpMethod.Put, SchedulePath(instanceId, scheduleId.ToString("D")), request, cancellationToken);
    }

    /// <summary>删除计划任务。</summary>
    public Task DeleteScheduleAsync(Guid instanceId, Guid scheduleId, CancellationToken cancellationToken)
    {
        return SendNoContentAsync(HttpMethod.Delete, SchedulePath(instanceId, scheduleId.ToString("D")), cancellationToken);
    }

    /// <summary>启用计划任务。</summary>
    public Task<ScheduledTaskDetailsDto> EnableScheduleAsync(
        Guid instanceId, Guid scheduleId, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<ScheduledTaskDetailsDto>(
            HttpMethod.Post, SchedulePath(instanceId, $"{scheduleId:D}/enable"), cancellationToken);
    }

    /// <summary>禁用计划任务。</summary>
    public Task<ScheduledTaskDetailsDto> DisableScheduleAsync(
        Guid instanceId, Guid scheduleId, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<ScheduledTaskDetailsDto>(
            HttpMethod.Post, SchedulePath(instanceId, $"{scheduleId:D}/disable"), cancellationToken);
    }

    /// <summary>立即运行计划任务。</summary>
    public Task<ScheduledTaskRunDto> RunScheduleAsync(
        Guid instanceId, Guid scheduleId, CancellationToken cancellationToken)
    {
        return SendNoBodyJsonAsync<ScheduledTaskRunDto>(
            HttpMethod.Post, SchedulePath(instanceId, $"{scheduleId:D}/run"), cancellationToken);
    }

    /// <summary>列出计划任务运行记录。</summary>
    public Task<CursorPage<ScheduledTaskRunDto>> ListScheduleRunsAsync(
        Guid instanceId, Guid scheduleId, CancellationToken cancellationToken)
    {
        return ListScheduleRunsAsync(instanceId, scheduleId, null, 50, cancellationToken);
    }

    /// <summary>分页列出计划任务运行记录。</summary>
    public Task<CursorPage<ScheduledTaskRunDto>> ListScheduleRunsAsync(
        Guid instanceId, Guid scheduleId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<ScheduledTaskRunDto>(
            SchedulePath(instanceId, $"{scheduleId:D}/runs") + "?cursor=" + Escape(cursor) + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    private static string ConfigurationPath(Guid instanceId, Guid nodeId, string? suffix = null)
    {
        return ResourcePath(instanceId, "configurations", suffix) + "?nodeId=" + nodeId.ToString("D");
    }

    private static string ModPath(Guid instanceId, Guid nodeId, string? suffix = null)
    {
        return ResourcePath(instanceId, "mods", suffix) + "?nodeId=" + nodeId.ToString("D");
    }

    private static string SchedulePath(Guid instanceId, string? suffix = null)
    {
        return ResourcePath(instanceId, "schedules", suffix);
    }

    private static string ResourcePath(Guid instanceId, string resource, string? suffix)
    {
        string path = $"api/v1/instances/{instanceId:D}/{resource}";
        return string.IsNullOrWhiteSpace(suffix) ? path : path + "/" + suffix;
    }
}

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.NetworkInformation;

using Google.Protobuf;

using Servitium.Agent.Instances;
using Servitium.Agent.Files;
using Servitium.Agent.Distribution;
using Servitium.Agent.Configurations;
using Servitium.Agent.Java;
using Servitium.Agent.Lifecycle;
using Servitium.Agent.Mods;
using Servitium.Agent.Scheduling;
using Servitium.Agent.Security;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Agent.V1;
using Servitium.Contracts.Instances;
using Servitium.Domain.Primitives;
using System.Collections.Generic;
using System.Linq;

namespace Servitium.Agent.ControlPlane;

internal sealed class AgentCommandDispatcher(
    InstanceRegistry instances,
    JavaRuntimeRegistry javaRuntimes,
    InstanceLifecycleService lifecycle,
    InstanceFileService files,
    AgentFileTaskCoordinator fileTasks,
    AgentUploadSessionService uploads,
    InboundFileTransferStore transfers,
    InstanceConfigurationService configurations,
    InstanceModService mods,
    LocalScheduler scheduler,
    AgentHmclDistributionService distribution,
    AgentCredentialStore credentials,
    Hosting.AgentNodeConfigurationStore nodeConfiguration)
{
    internal async Task<CommandResponse> DispatchAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Type switch
            {
                CommandType.CreateInstance => Success(request.CommandId, await CreateInstanceAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.UpdateInstance => Success(request.CommandId, await UpdateInstanceAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.UnregisterInstance => Success(request.CommandId, await UnregisterAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.DeleteWorkingDirectory => Success(request.CommandId, await DeleteWorkingDirectoryAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ListJava => Success(request.CommandId, await javaRuntimes.ListAsync(cancellationToken).ConfigureAwait(false)),
                CommandType.DiscoverJava => Success(request.CommandId, await javaRuntimes.DiscoverAsync(cancellationToken).ConfigureAwait(false)),
                CommandType.RegisterJava => Success(request.CommandId, await RegisterJavaAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.UnregisterJava => Success(request.CommandId, await UnregisterJavaAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.StartInstance => Success(request.CommandId, await StartInstanceAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.StopInstance => Success(request.CommandId, await StopInstanceAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.RestartInstance => Success(request.CommandId, await RestartInstanceAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ForceStopInstance => Success(request.CommandId, await ForceStopInstanceAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.SendCommand => Success(request.CommandId, await SendCommandAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.AcceptEula => Success(request.CommandId, await AcceptEulaAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.GetInstanceStatus => Success(request.CommandId, await GetInstanceStatusAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.QueryPlayers => Success(request.CommandId, await QueryPlayersAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.RconCommand => Success(request.CommandId, await ExecuteRconAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.RunUpdateAction => Success(request.CommandId, await RunUpdateActionAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ReadInstanceLog => Success(request.CommandId, await ReadInstanceLogAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ExportSessionLog => Success(request.CommandId, ExportSessionLog(request.Payload)),
                CommandType.ListDirectory => Success(request.CommandId, await ListDirectoryAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.GetFileSystemStatus => Success(request.CommandId, await GetFileSystemStatusAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.GetFileDownloadInfo => Success(request.CommandId, await GetFileDownloadInfoAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.CreateDirectory => Success(request.CommandId, await CreateDirectoryAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.CreateFile => Success(request.CommandId, await CreateFileAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.Rename => Success(request.CommandId, await RenameAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.Copy => Success(request.CommandId, await CopyAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.Move => Success(request.CommandId, await MoveAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.Delete => Success(request.CommandId, await DeleteAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.DeleteBatch => Success(request.CommandId, await DeleteBatchAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.Compress => Success(request.CommandId, await CompressAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.Extract => Success(request.CommandId, await ExtractAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ReadText => Success(request.CommandId, await ReadTextAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.WriteText => Success(request.CommandId, await WriteTextAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.DownloadFromUrl => Success(request.CommandId, await DownloadFromUrlAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.CompleteFileUpload => Success(request.CommandId, await CompleteFileUploadAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.StoreFileUploadPart => Success(request.CommandId, await StoreFileUploadPartAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.CompleteChunkedFileUpload => Success(request.CommandId, await CompleteChunkedFileUploadAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.CancelFileUpload => Success(request.CommandId, await CancelFileUploadAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ListConfigurations => Success(request.CommandId, await ListConfigurationsAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ReadConfiguration => Success(request.CommandId, await ReadConfigurationAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.WriteConfiguration => Success(request.CommandId, await WriteConfigurationAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ListConfigBackups => Success(request.CommandId, await ListConfigurationBackupsAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.RestoreConfigBackup => Success(request.CommandId, await RestoreConfigurationBackupAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ListMods => Success(request.CommandId, await ListModsAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.GetModMetadata => Success(request.CommandId, await GetModAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ToggleMod => Success(request.CommandId, await ToggleModAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.DeleteMod => Success(request.CommandId, await DeleteModAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ListModConfigFiles => Success(request.CommandId, await ListModConfigurationFilesAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.CompleteModUpload => Success(request.CommandId, await CompleteModUploadAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ListSchedules => Success(request.CommandId, await ListSchedulesAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.UpsertSchedule => Success(request.CommandId, await UpsertScheduleAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.DeleteSchedule => Success(request.CommandId, await DeleteScheduleAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.SetScheduleEnabled => Success(request.CommandId, await SetScheduleEnabledAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.RunScheduleNow => Success(request.CommandId, await RunScheduleNowAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.ListScheduleRuns => Success(request.CommandId, await ListScheduleRunsAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.GetMetrics => Success(request.CommandId, await GetMetricsAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.Reconcile => Success(request.CommandId, await ReconcileAsync(cancellationToken).ConfigureAwait(false)),
                CommandType.Unspecified => Failure(request.CommandId, "validation-failed"),
                CommandType.ImportJava => Success(request.CommandId, await ImportJavaAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.CancelFileTask => Success(request.CommandId, await CancelFileTaskAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.UpdateNodeConfig => Success(request.CommandId, await UpdateNodeConfigurationAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.RotateCredential => Success(request.CommandId, await RotateCredentialAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.CollectDirectoryForRelease => Success(request.CommandId, await CollectDirectoryForReleaseAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                CommandType.SyncFromRelease => Success(request.CommandId, await SyncFromReleaseAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                _ => Failure(request.CommandId, "validation-failed"),
            };
        }
        catch (DomainException exception)
        {
            return Failure(request.CommandId, exception.Code);
        }
        catch (JsonException)
        {
            return Failure(request.CommandId, "validation-failed");
        }
    }

    private Task<AgentCollectedDirectoryResult> CollectDirectoryForReleaseAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentCollectDirectoryForReleaseCommand command = JsonSerializer.Deserialize<AgentCollectDirectoryForReleaseCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The content collection command is invalid.");
        return distribution.CollectAsync(command, cancellationToken);
    }

    private Task<AgentSyncFromReleaseResult> SyncFromReleaseAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentSyncFromReleaseCommand command = JsonSerializer.Deserialize<AgentSyncFromReleaseCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The content sync command is invalid.");
        return distribution.SyncAsync(command, cancellationToken);
    }

    private Task<InstanceMetricsDto> GetMetricsAsync(ByteString payload, CancellationToken cancellationToken)
    {
        return lifecycle.GetMetricsAsync(DeserializeInstanceCommand(payload).InstanceId, cancellationToken);
    }

    private Task<InstanceUpdateResultDto> RunUpdateActionAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentRunInstanceUpdateCommand command = JsonSerializer.Deserialize<AgentRunInstanceUpdateCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The update-action command is invalid.");
        return lifecycle.RunUpdateActionAsync(command.InstanceId, command.Confirm, cancellationToken);
    }

    private Task<InstanceLogDto> ReadInstanceLogAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentReadInstanceLogCommand command = JsonSerializer.Deserialize<AgentReadInstanceLogCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The log query command is invalid.");
        return lifecycle.ReadLogAsync(command.InstanceId, command.RelativePath, command.StartLine, command.Count, command.Search, cancellationToken);
    }

    private SessionLogExportDto ExportSessionLog(ByteString payload)
    {
        AgentExportSessionLogCommand command = JsonSerializer.Deserialize<AgentExportSessionLogCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The session-log export command is invalid.");
        return lifecycle.ExportSessionLog(command.InstanceId, command.IncludeAnsi);
    }

    private async Task<object> RotateCredentialAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentRotateCredentialCommand command = JsonSerializer.Deserialize<AgentRotateCredentialCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The credential rotation command is invalid.");
        if (command.Revision <= 0 || string.IsNullOrWhiteSpace(command.Credential))
        {
            throw new DomainException("validation-failed", "The credential rotation material is invalid.");
        }

        await credentials.SaveAsync(command.Credential, cancellationToken).ConfigureAwait(false);
        return new { command.Revision };
    }

    private Task<Contracts.Nodes.UpdateNodeRequest> UpdateNodeConfigurationAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentUpdateNodeConfigurationCommand command = JsonSerializer.Deserialize<AgentUpdateNodeConfigurationCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The node configuration command is invalid.");
        return nodeConfiguration.UpdateAsync(command.Configuration, cancellationToken);
    }

    private async Task<AgentJavaRuntimeResponse> ImportJavaAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentImportJavaCommand command = JsonSerializer.Deserialize<AgentImportJavaCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The import-Java command is invalid.");
        if (string.IsNullOrWhiteSpace(command.UploadStreamId))
        {
            throw new DomainException("validation-failed", "The import-Java command has no completed upload.");
        }

        string archive = transfers.Consume(command.UploadStreamId);
        try
        {
            return await javaRuntimes.ImportAsync(archive, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (System.IO.File.Exists(archive))
            {
                System.IO.File.Delete(archive);
            }
        }
    }

    private Task<PlayerListDto> QueryPlayersAsync(ByteString payload, CancellationToken cancellationToken)
    {
        return lifecycle.QueryPlayersAsync(DeserializeInstanceCommand(payload).InstanceId, cancellationToken);
    }

    private Task<RconCommandResultDto> ExecuteRconAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentSendInstanceCommand command = JsonSerializer.Deserialize<AgentSendInstanceCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The RCON command is invalid.");
        return lifecycle.ExecuteRconAsync(command.InstanceId, command.Command, cancellationToken);
    }

    private Task<AgentInstanceResponse> UpdateInstanceAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentUpdateInstanceCommand command = JsonSerializer.Deserialize<AgentUpdateInstanceCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The update-instance command is invalid.");
        return UpdateInstanceCoreAsync(command, cancellationToken);
    }

    private async Task<AgentInstanceResponse> UpdateInstanceCoreAsync(AgentUpdateInstanceCommand command, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot updated = await instances.UpdateAsync(command.InstanceId, command.Request, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LocalInstanceSnapshot> all = await instances.ListAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(updated, all);
    }

    private async Task<Contracts.Files.FileEntryDto> CompleteFileUploadAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentCompleteFileUploadCommand command = JsonSerializer.Deserialize<AgentCompleteFileUploadCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The complete-upload command is invalid.");
        string stagingPath = transfers.Consume(command.UploadStreamId ?? string.Empty);
        try
        {
            return await files.CompleteUploadAsync(command.InstanceId, stagingPath, command.DestinationPath, command.Overwrite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (System.IO.File.Exists(stagingPath))
            {
                System.IO.File.Delete(stagingPath);
            }
        }
    }

    private async Task<object> StoreFileUploadPartAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentStoreFileUploadPartCommand command = JsonSerializer.Deserialize<AgentStoreFileUploadPartCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The store-upload-part command is invalid.");
        await uploads.StorePartAsync(command, cancellationToken).ConfigureAwait(false);
        return new { command.UploadId, command.PartIndex };
    }

    private Task<Contracts.Files.FileEntryDto> CompleteChunkedFileUploadAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentCompleteChunkedFileUploadCommand command = JsonSerializer.Deserialize<AgentCompleteChunkedFileUploadCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The complete-chunked-upload command is invalid.");
        return uploads.CompleteAsync(command, cancellationToken);
    }

    private async Task<object> CancelFileUploadAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentCancelFileUploadCommand command = JsonSerializer.Deserialize<AgentCancelFileUploadCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The cancel-file-upload command is invalid.");
        await uploads.DeleteAsync(command.InstanceId, command.UploadId, cancellationToken).ConfigureAwait(false);
        return new { command.UploadId };
    }

    private Task<Contracts.Files.FileEntryDto> CompressAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentCompressCommand command = JsonSerializer.Deserialize<AgentCompressCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The compression command is invalid.");
        return fileTasks.RunWithProgressAsync(
            command.TaskId ?? Guid.NewGuid(),
            command.InstanceId,
            "compress",
            command.DestinationPath,
            archive: true,
            urlDownload: false,
            (progress, token) => files.CompressAsync(command.InstanceId, command.RelativePaths, command.DestinationPath, command.EntryNameEncoding, progress, token),
            cancellationToken);
    }

    private Task<Contracts.Files.FileEntryDto> ExtractAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentExtractCommand command = JsonSerializer.Deserialize<AgentExtractCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The extraction command is invalid.");
        return fileTasks.RunWithProgressAsync(
            command.TaskId ?? Guid.NewGuid(),
            command.InstanceId,
            "extract",
            command.DestinationPath,
            archive: true,
            urlDownload: false,
            (progress, token) => files.ExtractAsync(command.InstanceId, command.ArchivePath, command.DestinationPath, command.EntryNameEncoding, progress, token),
            cancellationToken);
    }

    private Task<Contracts.Files.FileEntryDto> DownloadFromUrlAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentDownloadFromUrlCommand command = JsonSerializer.Deserialize<AgentDownloadFromUrlCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The URL download command is invalid.");
        return fileTasks.RunWithProgressAsync(
            command.TaskId ?? Guid.NewGuid(),
            command.InstanceId,
            "download-from-url",
            command.DestinationPath,
            archive: false,
            urlDownload: true,
            (progress, token) => files.DownloadFromUrlAsync(command.InstanceId, command.Url, command.DestinationPath, command.Overwrite, progress, token),
            cancellationToken);
    }

    private Task<IReadOnlyList<Contracts.Configurations.ConfigurationDto>> ListConfigurationsAsync(ByteString payload, CancellationToken cancellationToken)
    {
        return configurations.ListAsync(DeserializeInstanceCommand(payload).InstanceId, cancellationToken);
    }

    private Task<Contracts.Configurations.ConfigurationDetailsDto> ReadConfigurationAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFilePathCommand command = DeserializeFilePathCommand(payload);
        return configurations.ReadAsync(command.InstanceId, command.RelativePath, cancellationToken);
    }

    private Task<Contracts.Configurations.ConfigurationDetailsDto> WriteConfigurationAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentWriteConfigurationCommand command = JsonSerializer.Deserialize<AgentWriteConfigurationCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The configuration write command is invalid.");
        return configurations.WriteAsync(command.InstanceId, command.ConfigKey, command.Request, cancellationToken);
    }

    private Task<IReadOnlyList<Contracts.Configurations.ConfigurationBackupDto>> ListConfigurationBackupsAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFilePathCommand command = DeserializeFilePathCommand(payload);
        return configurations.ListBackupsAsync(command.InstanceId, command.RelativePath, cancellationToken);
    }

    private Task<Contracts.Configurations.ConfigurationDetailsDto> RestoreConfigurationBackupAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentConfigurationBackupCommand command = JsonSerializer.Deserialize<AgentConfigurationBackupCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The configuration restore command is invalid.");
        return configurations.RestoreBackupAsync(command.InstanceId, command.ConfigKey, command.BackupId, cancellationToken);
    }

    private Task<IReadOnlyList<Contracts.Mods.ModDetailsDto>> ListModsAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFilePathCommand command = DeserializeFilePathCommand(payload);
        return mods.ListAsync(command.InstanceId, command.RelativePath, cancellationToken);
    }

    private Task<Contracts.Mods.ModDetailsDto> GetModAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentModCommand command = JsonSerializer.Deserialize<AgentModCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The mod command is invalid.");
        return mods.GetAsync(command.InstanceId, command.ModId, command.Kind, cancellationToken);
    }

    private Task<Contracts.Mods.ModDetailsDto> ToggleModAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentModToggleCommand command = JsonSerializer.Deserialize<AgentModToggleCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The mod toggle command is invalid.");
        return mods.SetEnabledAsync(command.InstanceId, command.ModId, command.Kind, command.Enabled, cancellationToken);
    }

    private async Task<object> DeleteModAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentModCommand command = JsonSerializer.Deserialize<AgentModCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The mod delete command is invalid.");
        await mods.DeleteAsync(command.InstanceId, command.ModId, command.Kind, cancellationToken).ConfigureAwait(false);
        return new { command.ModId };
    }

    private Task<IReadOnlyList<string>> ListModConfigurationFilesAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentModCommand command = JsonSerializer.Deserialize<AgentModCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The mod configuration command is invalid.");
        return mods.ListConfigurationFilesAsync(command.InstanceId, command.ModId, command.Kind, cancellationToken);
    }

    private async Task<Contracts.Mods.ModDetailsDto> CompleteModUploadAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentCompleteModUploadCommand command = JsonSerializer.Deserialize<AgentCompleteModUploadCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The complete-mod-upload command is invalid.");
        string destination = await mods.PrepareUploadAsync(command.InstanceId, command.Kind, command.FileName, cancellationToken).ConfigureAwait(false);
        _ = await uploads.CompleteAsync(
            new AgentCompleteChunkedFileUploadCommand(
                command.InstanceId,
                command.UploadId,
                destination,
                command.PartCount,
                command.TotalBytes,
                command.Sha256,
                command.Overwrite),
            cancellationToken).ConfigureAwait(false);
        string modId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(destination.ToUpperInvariant())))[..24];
        return await mods.GetAsync(command.InstanceId, modId, command.Kind, cancellationToken).ConfigureAwait(false);
    }

    private Task<IReadOnlyList<Contracts.Scheduling.ScheduledTaskDetailsDto>> ListSchedulesAsync(ByteString payload, CancellationToken cancellationToken)
    {
        return scheduler.ListAsync(DeserializeInstanceCommand(payload).InstanceId, cancellationToken);
    }

    private Task<Contracts.Scheduling.ScheduledTaskDetailsDto> UpsertScheduleAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentUpsertScheduleCommand command = JsonSerializer.Deserialize<AgentUpsertScheduleCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The schedule upsert command is invalid.");
        return scheduler.UpsertAsync(command.InstanceId, command.TaskId, command.Request, cancellationToken);
    }

    private async Task<object> DeleteScheduleAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentScheduleIdCommand command = JsonSerializer.Deserialize<AgentScheduleIdCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The schedule delete command is invalid.");
        await scheduler.DeleteAsync(command.InstanceId, command.TaskId, cancellationToken).ConfigureAwait(false);
        return new { command.TaskId };
    }

    private Task<Contracts.Scheduling.ScheduledTaskDetailsDto> SetScheduleEnabledAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentSetScheduleEnabledCommand command = JsonSerializer.Deserialize<AgentSetScheduleEnabledCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The schedule state command is invalid.");
        return scheduler.SetEnabledAsync(command.InstanceId, command.TaskId, command.Enabled, cancellationToken);
    }

    private Task<Contracts.Scheduling.ScheduledTaskRunDto> RunScheduleNowAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentScheduleIdCommand command = JsonSerializer.Deserialize<AgentScheduleIdCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The schedule run command is invalid.");
        return scheduler.RunNowAsync(command.InstanceId, command.TaskId, cancellationToken);
    }

    private Task<IReadOnlyList<Contracts.Scheduling.ScheduledTaskRunDto>> ListScheduleRunsAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentScheduleIdCommand command = JsonSerializer.Deserialize<AgentScheduleIdCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The schedule runs command is invalid.");
        return scheduler.ListRunsAsync(command.InstanceId, command.TaskId, cancellationToken);
    }

    private Task<Contracts.Files.FileEntryPageDto> ListDirectoryAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentListDirectoryCommand command = JsonSerializer.Deserialize<AgentListDirectoryCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The directory query is invalid.");
        return files.ListAsync(command.InstanceId, command.RelativePath, command.Cursor, command.PageSize, command.Search, command.Sort, command.Descending, cancellationToken);
    }

    private Task<Contracts.Files.FileSystemStatusDto> GetFileSystemStatusAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentInstanceIdCommand command = DeserializeInstanceCommand(payload);
        return files.GetStatusAsync(command.InstanceId, cancellationToken);
    }

    private Task<Contracts.Files.FileDownloadInfoDto> GetFileDownloadInfoAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFilePathCommand command = DeserializeFilePathCommand(payload);
        return files.GetDownloadInfoAsync(command.InstanceId, command.RelativePath, cancellationToken);
    }

    private Task<Contracts.Files.FileEntryDto> CreateDirectoryAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFilePathCommand command = DeserializeFilePathCommand(payload);
        return files.CreateDirectoryAsync(command.InstanceId, command.RelativePath, cancellationToken);
    }

    private Task<Contracts.Files.FileEntryDto> CreateFileAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFilePathCommand command = DeserializeFilePathCommand(payload);
        return files.CreateFileAsync(command.InstanceId, command.RelativePath, cancellationToken);
    }

    private Task<Contracts.Files.FileEntryDto> CopyAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFileTransferCommand command = DeserializeFileTransferCommand(payload);
        return fileTasks.RunWithProgressAsync(
            command.TaskId ?? Guid.NewGuid(),
            command.InstanceId,
            "copy",
            command.DestinationPath,
            archive: false,
            urlDownload: false,
            (progress, token) => files.CopyAsync(command.InstanceId, command.SourcePath, command.DestinationPath, command.CollisionPolicy, progress, token),
            cancellationToken);
    }

    private Task<Contracts.Files.FileEntryDto> MoveAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFileTransferCommand command = DeserializeFileTransferCommand(payload);
        return fileTasks.RunWithProgressAsync(
            command.TaskId ?? Guid.NewGuid(),
            command.InstanceId,
            "move",
            command.DestinationPath,
            archive: false,
            urlDownload: false,
            (progress, token) => files.MoveAsync(command.InstanceId, command.SourcePath, command.DestinationPath, command.CollisionPolicy, progress, token),
            cancellationToken);
    }

    private Task<Contracts.Files.FileEntryDto> RenameAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentRenameFileCommand command = JsonSerializer.Deserialize<AgentRenameFileCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The rename command is invalid.");
        return files.RenameAsync(command.InstanceId, command.RelativePath, command.NewName, cancellationToken);
    }

    private async Task<object> DeleteAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFilePathCommand command = DeserializeFilePathCommand(payload);
        return await fileTasks.RunWithProgressAsync<object>(
            command.TaskId ?? Guid.NewGuid(),
            command.InstanceId,
            "delete",
            command.RelativePath,
            archive: false,
            urlDownload: false,
            async (progress, token) =>
            {
                await files.DeleteAsync(command.InstanceId, command.RelativePath, progress, token).ConfigureAwait(false);
                return new { command.RelativePath };
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> DeleteBatchAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentFileBatchCommand command = JsonSerializer.Deserialize<AgentFileBatchCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The batch-delete command is invalid.");
        return command.RelativePaths.Count == 0
            ? throw new DomainException("validation-failed", "The batch-delete command has no paths.")
            : await fileTasks.RunWithProgressAsync<object>(
            command.TaskId ?? Guid.NewGuid(),
            command.InstanceId,
            "delete-batch",
            command.RelativePaths[0],
            archive: false,
            urlDownload: false,
            async (progress, token) =>
            {
                await files.DeleteManyAsync(command.InstanceId, command.RelativePaths, progress, token).ConfigureAwait(false);
                return new { count = command.RelativePaths.Count };
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> CancelFileTaskAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentCancelFileTaskCommand command = JsonSerializer.Deserialize<AgentCancelFileTaskCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The cancel-file-task command is invalid.");
        await fileTasks.CancelAsync(command.InstanceId, command.TaskId, cancellationToken).ConfigureAwait(false);
        return new { command.TaskId };
    }

    private Task<AgentTextFileResponse> ReadTextAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentReadTextCommand command = JsonSerializer.Deserialize<AgentReadTextCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The read-text command is invalid.");
        return files.ReadTextAsync(command.InstanceId, command.RelativePath, command.Encoding, cancellationToken);
    }

    private Task<AgentTextFileResponse> WriteTextAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentWriteTextCommand command = JsonSerializer.Deserialize<AgentWriteTextCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The write-text command is invalid.");
        return files.WriteTextAsync(command.InstanceId, command.RelativePath, command.Content, command.Encoding, cancellationToken);
    }

    private static AgentFilePathCommand DeserializeFilePathCommand(ByteString payload)
    {
        return JsonSerializer.Deserialize<AgentFilePathCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The file-path command is invalid.");
    }

    private static AgentFileTransferCommand DeserializeFileTransferCommand(ByteString payload)
    {
        return JsonSerializer.Deserialize<AgentFileTransferCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The file-transfer command is invalid.");
    }

    private Task<AgentInstanceRuntimeResponse> StartInstanceAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentInstanceIdCommand command = DeserializeInstanceCommand(payload);
        return lifecycle.StartInstanceAsync(command.InstanceId, cancellationToken);
    }

    private Task<AgentInstanceRuntimeResponse> StopInstanceAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentStopInstanceCommand command = JsonSerializer.Deserialize<AgentStopInstanceCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The stop-instance command is invalid.");
        return lifecycle.StopInstanceAsync(command.InstanceId, command.StopCommand, command.StopTimeoutSeconds, cancellationToken);
    }

    private async Task<AgentInstanceRuntimeResponse> RestartInstanceAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentInstanceIdCommand command = DeserializeInstanceCommand(payload);
        return await lifecycle.RestartInstanceAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
    }

    private Task<AgentInstanceRuntimeResponse> ForceStopInstanceAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentInstanceIdCommand command = DeserializeInstanceCommand(payload);
        return lifecycle.ForceStopInstanceAsync(command.InstanceId, cancellationToken);
    }

    private Task<AgentInstanceRuntimeResponse> SendCommandAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentSendInstanceCommand command = JsonSerializer.Deserialize<AgentSendInstanceCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The send-command request is invalid.");
        return lifecycle.SendCommandAsync(command.InstanceId, command.Command, cancellationToken);
    }

    private async Task<object> AcceptEulaAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentInstanceIdCommand command = DeserializeInstanceCommand(payload);
        await lifecycle.AcceptEulaAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
        return new { command.InstanceId };
    }

    private Task<AgentInstanceRuntimeResponse> GetInstanceStatusAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentInstanceIdCommand command = DeserializeInstanceCommand(payload);
        return lifecycle.GetStatusAsync(command.InstanceId, cancellationToken);
    }

    private static AgentInstanceIdCommand DeserializeInstanceCommand(ByteString payload)
    {
        return JsonSerializer.Deserialize<AgentInstanceIdCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The instance command is invalid.");
    }

    private async Task<AgentJavaRuntimeResponse> RegisterJavaAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentRegisterJavaCommand command = JsonSerializer.Deserialize<AgentRegisterJavaCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The register-Java command is invalid.");
        return await javaRuntimes.RegisterAsync(command.ExecutablePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> UnregisterJavaAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentUnregisterJavaCommand command = JsonSerializer.Deserialize<AgentUnregisterJavaCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The unregister-Java command is invalid.");
        await javaRuntimes.UnregisterAsync(command.RuntimeId, command.DeleteFiles, command.Force, cancellationToken).ConfigureAwait(false);
        return new { command.RuntimeId };
    }

    private async Task<AgentInstanceResponse> CreateInstanceAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentCreateInstanceCommand command = JsonSerializer.Deserialize<AgentCreateInstanceCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The create-instance command is invalid.");
        LocalInstanceSnapshot result;
        switch (command.Mode)
        {
            case AgentInstanceCreationMode.Empty:
                result = await instances.CreateEmptyAsync(command.InstanceId, command.Name, command.Launch, command.Tags, command.Notes, DateTimeOffset.UtcNow, cancellationToken, command.InitialConfiguration).ConfigureAwait(false);
                break;
            case AgentInstanceCreationMode.AttachExternal:
                result = await instances.AttachExternalAsync(command.InstanceId, command.Name, command.ExternalPath ?? string.Empty, command.Launch, DateTimeOffset.UtcNow, cancellationToken, command.InitialConfiguration).ConfigureAwait(false);
                break;
            case AgentInstanceCreationMode.ServerArchive:
                string archivePath = transfers.Consume(command.UploadStreamId ?? string.Empty);
                try
                {
                    result = await instances.ImportServerArchiveAsync(command.InstanceId, command.Name, archivePath, command.Launch, DateTimeOffset.UtcNow, cancellationToken, command.InitialConfiguration).ConfigureAwait(false);
                }
                finally
                {
                    if (System.IO.File.Exists(archivePath))
                    {
                        System.IO.File.Delete(archivePath);
                    }
                }

                break;
            default:
                throw new DomainException("validation-failed", "The instance creation mode is invalid.");
        }

        IReadOnlyList<LocalInstanceSnapshot> all = await instances.ListAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(result, all);
    }

    private async Task<object> UnregisterAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentInstanceIdCommand command = JsonSerializer.Deserialize<AgentInstanceIdCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The instance command is invalid.");
        await instances.UnregisterAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
        return new { command.InstanceId };
    }

    private async Task<object> DeleteWorkingDirectoryAsync(ByteString payload, CancellationToken cancellationToken)
    {
        AgentInstanceIdCommand command = JsonSerializer.Deserialize<AgentInstanceIdCommand>(payload.Span)
            ?? throw new DomainException("validation-failed", "The instance command is invalid.");
        await instances.DeleteOwnedWorkingDirectoryAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
        return new { command.InstanceId };
    }

    private async Task<AgentReconcileResponse> ReconcileAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalInstanceSnapshot> snapshots = await instances.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AgentJavaRuntimeResponse> java = await javaRuntimes.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Contracts.Scheduling.ScheduledTaskDetailsDto> schedules = await scheduler.ListAllAsync(cancellationToken).ConfigureAwait(false);
        return new([.. Enumerable.Select(snapshots, snapshot => ToResponse(snapshot, snapshots))], java, schedules, DateTimeOffset.UtcNow);
    }

    private static AgentInstanceResponse ToResponse(LocalInstanceSnapshot instance, IReadOnlyList<LocalInstanceSnapshot> all)
    {
        LocalInstanceRuntimeSettings settings = instance.RuntimeSettings;
        UpdateInstanceRequest configuration = new(
            instance.Name,
            instance.Tags,
            instance.Notes,
            instance.Launch,
            settings.StopCommand,
            settings.StopTimeoutSeconds,
            settings.AutoStart,
            settings.AutoRestart,
            settings.MaximumRestartAttempts,
            settings.Rcon with { Password = null },
            settings.Query,
            instance.WorkingDirectory,
            instance.PrimaryPort,
            settings.AutoStartDelaySeconds,
            settings.RestartIntervalSeconds,
            settings.RestartResetSeconds,
            settings.RunCredentialAlias,
            settings.Terminal ?? new(),
            settings.UpdateAction ?? new(),
            settings.ShortcutCommands ?? []);
        List<string> warnings = [];
        if (instance.PrimaryPort is int port)
        {
            bool duplicate = all.Any(item => item.Id != instance.Id && item.PrimaryPort == port);
            bool listener = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
            if (duplicate || listener)
            {
                warnings.Add("primary-port-in-use");
            }
        }

        return new(instance.Id, instance.Name, instance.WorkingDirectory, instance.OwnedByServitium, instance.Status, instance.Launch, instance.Tags, instance.Notes, configuration, instance.Revision, warnings, instance.StartedAt);
    }

    private static CommandResponse Success<T>(string commandId, T payload)
    {
        return new CommandResponse
        {
            CommandId = commandId,
            Succeeded = true,
            Accepted = true,
            Payload = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(payload)),
        };
    }

    private static CommandResponse Failure(string commandId, string code)
    {
        return new CommandResponse
        {
            CommandId = commandId,
            Succeeded = false,
            Error = new AgentError { Code = code },
        };
    }
}

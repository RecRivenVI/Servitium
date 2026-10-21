using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Auditing;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Files;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Scheduling;
using Servitium.Contracts.Settings;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.ViewModels;

internal sealed partial class PageActionDispatcher(AppServices services)
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);
    private object? _selected;
    private IReadOnlyList<object> _selectedBatch = [];
    private HashSet<Guid> _currentGrantIds = [];
    private ConfigurationDto? _selectedConfiguration;
    private ConfigurationBackupDto? _selectedConfigurationBackup;
    private TextFileDto? _selectedTextFile;
    private StagedPrismPreviewDto? _stagedPrismPreview;
    private string _directoryPath = string.Empty;
    private readonly List<string> _directoryTabs = [string.Empty];
    private int _activeDirectoryTab;
    private FileClipboardPlan? _fileClipboard;
    private string? _configurationRevision;
    private readonly Services.TransferPauseGate _transferPause = new();

    internal event EventHandler<TransferProgressEventArgs>? TransferProgressChanged;

    internal void PauseTransfer()
    {
        _transferPause.Pause();
    }

    internal void ResumeTransfer()
    {
        _transferPause.Resume();
    }

    internal string EditorLanguage => ConfigurationLanguage(_selectedTextFile?.Path ?? _selectedConfiguration?.RelativePath);

    internal ConfigurationDetailsDto? ConfigurationDetails { get; private set; }

    internal IReadOnlyList<ConfigurationFieldDto> ConfigurationSchema { get; private set; } = [];

    internal bool ConfigurationRawMode { get; private set; }

    internal void ToggleConfigurationView()
    {
        ConfigurationRawMode = !ConfigurationRawMode;
    }

    private static string ConfigurationLanguage(string? path)
    {
        return Path.GetExtension(path)?.ToUpperInvariant() switch
        {
            ".PROPERTIES" => "properties",
            ".JSON" => "json",
            ".YAML" or ".YML" => "yaml",
            ".TOML" => "toml",
            _ => "plaintext",
        };
    }

    internal void Select(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _selected = value;
        _selectedBatch = [value];
        switch (value)
        {
            case Contracts.Nodes.NodeDto node:
                services.Session.SelectedNode = node;
                break;
            case InstanceDto instance:
                services.Session.SelectedInstance = instance;
                break;
            case HmclChannelDto channel:
                services.Session.SelectedChannel = channel;
                break;
            case HmclReleaseSummaryDto release:
                services.Session.SelectedRelease = release;
                break;
            case UserDto user:
                services.Session.SelectedUser = user;
                break;
            case ConfigurationDto configuration:
                _selectedConfiguration = configuration;
                _selectedConfigurationBackup = null;
                ConfigurationDetails = null;
                ConfigurationSchema = [];
                ConfigurationRawMode = configuration.Format != "properties";
                break;
            case ConfigurationBackupDto backup:
                _selectedConfigurationBackup = backup;
                break;
            case FileEntryDto file:
                if (!string.Equals(_selectedTextFile?.Path, file.Path, StringComparison.Ordinal))
                {
                    _selectedTextFile = null;
                }
                break;
            default:
                break;
        }
    }

    internal void SelectMany(IReadOnlyList<object> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _selectedBatch = values;
        if (values.Count > 0)
        {
            object selected = values[^1];
            Select(selected);
            _selectedBatch = values;
        }
        else
        {
            _selected = null;
        }
    }

    internal IReadOnlyDictionary<string, string> SelectionFields()
    {
        return _selected switch
        {
            UserDto user => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["user-name"] = user.UserName,
                ["display-name"] = user.DisplayName,
                ["role"] = user.Role,
                ["password"] = string.Empty,
            },
            ScheduledTaskDetailsDto task => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = task.Name,
                ["trigger"] = FormatScheduleTrigger(task.Trigger),
                ["actions"] = string.Join(Environment.NewLine, task.Actions.Select(value => value.Kind + ":" + value.Argument)),
                ["remaining-runs"] = task.RemainingRuns.ToString(CultureInfo.InvariantCulture),
                ["overlap-policy"] = task.OverlapPolicy,
            },
            _ => new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    internal string? ActivateSelection(object value)
    {
        Select(value);
        return value switch
        {
            FileEntryDto { IsDirectory: true } directory => SetDirectory(directory.Path),
            DirectoryTab tab => SelectDirectoryTab(tab),
            InstanceDto => "PG-012",
            ConfigurationDto => "PG-015",
            Contracts.Nodes.NodeDto => "PG-021",
            UserDto => "PG-031",
            HmclChannelDto => "PG-061",
            HmclReleaseSummaryDto => "PG-063",
            _ => null,
        };
    }

    private string? SetDirectory(string path)
    {
        _directoryPath = path;
        _directoryTabs[_activeDirectoryTab] = path;
        return null;
    }

    private string? SelectDirectoryTab(DirectoryTab tab)
    {
        if (tab.Index < 0 || tab.Index >= _directoryTabs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(tab));
        }

        _activeDirectoryTab = tab.Index;
        _directoryPath = _directoryTabs[_activeDirectoryTab];
        return null;
    }

    internal async Task<PageSnapshot> LoadAsync(
        string pageId,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(fields);
        return !services.Session.IsConnected
            ? Empty("shell.status-disconnected")
            : pageId is "PG-001" or "PG-002" or "PG-003"
            ? await LoadAuthenticationAsync(pageId, cancellationToken).ConfigureAwait(false)
            : services.Session.Principal is null && pageId is not ("PG-050" or "PG-051")
            ? Empty("shell.status-signed-out")
            : pageId switch
            {
                "PG-010" or "PG-011" => await LoadInstancesAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-012" => await LoadInstanceConsoleAsync(cancellationToken).ConfigureAwait(false),
                "PG-013" => await LoadFilesAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-014" => await LoadConfigurationsAsync(cancellationToken).ConfigureAwait(false),
                "PG-015" => await LoadConfigurationEditorAsync(cancellationToken).ConfigureAwait(false),
                "PG-016" => await LoadModsAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-017" => await LoadSchedulesAsync(cancellationToken).ConfigureAwait(false),
                "PG-018" => await LoadInstanceSettingsAsync(cancellationToken).ConfigureAwait(false),
                "PG-019" => await LoadInstanceCreationAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-020" => await LoadNodesAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-021" => await LoadNodeDetailsAsync(cancellationToken).ConfigureAwait(false),
                "PG-022" => await LoadJavaAsync(cancellationToken).ConfigureAwait(false),
                "PG-030" => await LoadUsersAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-031" => await LoadGrantsAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-032" => await LoadAccountAsync(cancellationToken).ConfigureAwait(false),
                "PG-040" => await LoadDashboardAsync(cancellationToken).ConfigureAwait(false),
                "PG-041" => await LoadSettingsAsync(cancellationToken).ConfigureAwait(false),
                "PG-042" => await LoadActivityAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-043" => await LoadAuditAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-044" => await LoadOperationsAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-060" => await LoadDistributionAsync(cancellationToken).ConfigureAwait(false),
                "PG-061" => await LoadChannelAsync(cancellationToken).ConfigureAwait(false),
                "PG-062" => await LoadReleaseCreationAsync(fields, cancellationToken).ConfigureAwait(false),
                "PG-063" => await LoadReleaseAsync(cancellationToken).ConfigureAwait(false),
                "PG-050" => await LoadPublicAsync(cancellationToken).ConfigureAwait(false),
                "PG-051" => Empty(),
                _ => throw new ArgumentOutOfRangeException(nameof(pageId)),
            };
    }

    internal async Task<PageSectionSnapshot> LoadMoreAsync(
        string pageId,
        string section,
        string cursor,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentNullException.ThrowIfNull(fields);
        InstanceDto? instance = services.Session.SelectedInstance;

        if (pageId is "PG-010" or "PG-011" && section == "instance-list")
        {
            Guid? nodeId = Guid.TryParse(Value(fields, "node"), out Guid parsedNode) ? parsedNode : null;
            CursorPage<InstanceDto> page = await services.Session.Client.ListInstancesAsync(
                nodeId, Value(fields, "status"), Value(fields, "query"), Split(fields, "tags"), cursor, 100, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Name, item => DisplayValue("status", item.Status) + " · " + item.WorkingDirectory), page.NextCursor);
        }

        if (pageId == "PG-020" && section == "node-list")
        {
            CursorPage<Contracts.Nodes.NodeDto> page = await services.Session.Client.ListNodesAsync(
                Value(fields, "status"), Value(fields, "query"), cursor, 100, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Name, item => DisplayValue("status", item.Status)), page.NextCursor);
        }

        if (pageId == "PG-013" && section == "file-list" && instance is not null)
        {
            FileEntryPageDto page = await services.Session.Client.ListFilesAsync(
                instance.Id,
                instance.NodeId,
                _directoryPath,
                cursor,
                200,
                Value(fields, "search"),
                Value(fields, "sort", "name"),
                Boolean(fields, "descending", false),
                cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Path, item => item.IsDirectory ? services.Localization.Get("common.directory") : FormatBytes(item.Size)), page.NextCursor);
        }

        if (pageId == "PG-014" && section == "configuration-list" && instance is not null)
        {
            CursorPage<ConfigurationDto> page = await services.Session.Client.ListConfigurationsAsync(instance.Id, instance.NodeId, cursor, 50, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.RelativePath, item => item.Format), page.NextCursor);
        }

        if (pageId == "PG-015" && section == "backups" && instance is not null && _selectedConfiguration is not null)
        {
            CursorPage<ConfigurationBackupDto> page = await services.Session.Client.ListConfigurationBackupsAsync(
                instance.Id, instance.NodeId, _selectedConfiguration.Key, cursor, 50, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.CreatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture), item => FormatBytes(item.Size)), page.NextCursor);
        }

        if (pageId == "PG-016" && section == "mod-list" && instance is not null)
        {
            CursorPage<ModDetailsDto> page = await services.Session.Client.ListModsAsync(
                instance.Id, instance.NodeId, Value(fields, "type", "mods"), Value(fields, "query"), cursor, 50, cancellationToken).ConfigureAwait(false);
            bool? enabled = bool.TryParse(Value(fields, "enabled"), out bool parsedEnabled) ? parsedEnabled : null;
            IReadOnlyList<ModDetailsDto> items = enabled is null ? page.Items : [.. page.Items.Where(item => item.Enabled == enabled)];
            return new(Items(items, item => item.Name, item => (item.Version ?? string.Empty) + " · " + services.Localization.Get(item.Enabled ? "state.enabled" : "state.disabled")), page.NextCursor);
        }

        if (pageId == "PG-017" && section == "task-list" && instance is not null)
        {
            CursorPage<ScheduledTaskDetailsDto> page = await services.Session.Client.ListSchedulesAsync(instance.Id, cursor, 50, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Name, item => services.Localization.Get(item.Enabled ? "state.enabled" : "state.disabled")), page.NextCursor);
        }

        if (pageId == "PG-021" && section == "metrics")
        {
            CursorPage<Contracts.Nodes.NodeMetricsDto> page = await services.Session.Client.ListNodeMetricsAsync(RequireNode().Id, null, cursor, 200, cancellationToken).ConfigureAwait(false);
            return new([.. page.Items.Select(item => Item(item.SampledAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture), item.CpuPercent.ToString("F1", CultureInfo.CurrentCulture) + "% · " + FormatBytes(item.MemoryUsedBytes), item))], page.NextCursor);
        }

        if (pageId == "PG-022" && section == "runtime-list")
        {
            CursorPage<JavaRuntimeDto> page = await services.Session.Client.ListJavaRuntimesAsync(RequireNode().Id, cursor, 50, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.DisplayName ?? item.Version, item => item.ExecutablePath), page.NextCursor);
        }

        if (pageId == "PG-030" && section == "user-list")
        {
            CursorPage<UserDto> page = await services.Session.Client.ListUsersAsync(Value(fields, "role"), Value(fields, "query"), cursor, 100, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.DisplayName, item => item.UserName + " · " + DisplayValue("role", item.Role)), page.NextCursor);
        }

        if (pageId == "PG-032" && section == "sessions")
        {
            CursorPage<SessionDto> page = await services.Session.Client.ListSessionsAsync(cursor, 50, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.CreatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture), item => item.Current ? services.Localization.Get("state.current") : item.ExpiresAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), page.NextCursor);
        }

        if (pageId == "PG-032" && section == "api-keys")
        {
            CursorPage<ApiKeyDto> page = await services.Session.Client.ListApiKeysAsync(cursor, 50, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Name, item => item.Scope), page.NextCursor);
        }

        if (pageId == "PG-042" && section == "log-list")
        {
            CursorPage<ActivityLogDto> page = await services.Session.Client.ListActivityLogsAsync(
                ActivityFilter(fields) with { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Action, item => item.ActorName + " · " + item.OccurredAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), page.NextCursor);
        }

        if (pageId == "PG-043" && section == "audit-list")
        {
            CursorPage<AuditLogDto> page = await services.Session.Client.ListSecurityAuditAsync(
                SecurityFilter(fields) with { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Action, item => DisplayValue("audit-category", item.Category) + " · " + DisplayValue("outcome", item.OutcomeCode)), page.NextCursor);
        }

        if (pageId == "PG-044" && section == "operation-list")
        {
            CursorPage<OperationDetailsDto> page = await services.Session.Client.ListOperationsAsync(
                Value(fields, "status"), Value(fields, "kind"), null, null, cursor, 50, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => DisplayValue("operation-kind", item.Operation.Kind), item => DisplayValue("status", item.Operation.Status) + " · " + item.Target), page.NextCursor);
        }

        if (pageId == "PG-061" && section == "release-history")
        {
            CursorPage<HmclReleaseSummaryDto> page = await services.Session.Client.ListHmclReleasesAsync(RequireChannel().Id, cursor, 50, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Name + " " + item.Version, item => item.IsActive ? services.Localization.Get("state.active") : item.CreatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), page.NextCursor);
        }

        if (pageId == "PG-063" && section == "files")
        {
            HmclReleaseSummaryDto release = services.Session.SelectedRelease ?? throw SelectionRequired();
            HmclReleaseFilePageDto page = await services.Session.Client.ListHmclReleaseFilesAsync(
                release.Id, Value(fields, "query"), cursor, 200, cancellationToken).ConfigureAwait(false);
            return new(Items(page.Items, item => item.Path, item => FormatBytes(item.Size)), page.NextCursor);
        }

        throw new InvalidOperationException(services.Localization.Get("error.validation-failed"));
    }

    private async Task<PageSnapshot> LoadAuthenticationAsync(string pageId, CancellationToken cancellationToken)
    {
        AuthenticationBootstrapDto bootstrap = await services.Session.Client.GetAuthenticationBootstrapAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["language"] = services.Settings.Current.Language,
            ["user-name"] = string.Empty,
        };
        List<PageListItem> external = [];
        if (bootstrap.OidcEnabled)
        {
            external.Add(Item(bootstrap.OidcDisplayName ?? "OIDC", bootstrap.LoginPageText, bootstrap));
        }

        return pageId switch
        {
            "PG-001" => Snapshot(fields, ("prerequisites", [Item(
                services.Localization.Get(bootstrap.Initialized ? "state.initialized" : "state.not-initialized"),
                bootstrap.LoginPageText,
                bootstrap)])),
            "PG-002" => Snapshot(fields, ("external-login", external)),
            _ => Snapshot(fields),
        };
    }

    private async Task<PageSnapshot> LoadInstancesAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        Guid? nodeId = Guid.TryParse(Value(fields, "node"), out Guid parsedNode) ? parsedNode : services.Session.PendingInstanceNodeFilter;
        services.Session.PendingInstanceNodeFilter = null;
        string[] tags = Split(fields, "tags");
        CursorPage<InstanceDto> page = await services.Session.Client.ListInstancesAsync(
            nodeId, Value(fields, "status"), Value(fields, "query"), tags, null, 100, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = Value(fields, "query"),
                ["node"] = Value(fields, "node"),
                ["status"] = Value(fields, "status"),
                ["tags"] = Value(fields, "tags"),
            },
            ("instance-list", Items(page.Items, item => item.Name, item => DisplayValue("status", item.Status) + " · " + item.WorkingDirectory), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadInstanceConsoleAsync(CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        InstanceRuntimeDto status = await services.Session.Client.GetInstanceStatusAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false);
        InstanceMetricsDto metrics = await services.Session.Client.GetInstanceMetricsAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false);
        PlayerListDto players = await services.Session.Client.GetInstancePlayersAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false);
        return Snapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            ("summary", [Item(instance.Name, DisplayValue("status", status.Status), instance)]),
            ("console", status.RecentOutput.Select(line => Item(line, null, line)).ToArray()),
            ("resource-usage", [Item(
                metrics.CpuPercent.ToString("F1", CultureInfo.CurrentCulture) + "%",
                FormatBytes(metrics.MemoryBytes) + " · " + players.Online.ToString(CultureInfo.CurrentCulture) + "/" + players.Maximum.ToString(CultureInfo.CurrentCulture),
                metrics)]));
    }

    private async Task<PageSnapshot> LoadFilesAsync(
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        string requestedPath = Value(fields, "path", _directoryPath).Replace('\\', '/').Trim('/');
        _directoryPath = requestedPath.Length == 0 ? string.Empty : CombineRelative(string.Empty, requestedPath);
        _directoryTabs[_activeDirectoryTab] = _directoryPath;
        string sort = Value(fields, "sort", "name");
        bool descending = Boolean(fields, "descending", false);
        FileEntryPageDto page = await services.Session.Client.ListFilesAsync(
            instance.Id, instance.NodeId, _directoryPath, null, 200, Value(fields, "search"), sort, descending, cancellationToken).ConfigureAwait(false);
        FileSystemStatusDto status = await services.Session.Client.GetFileSystemStatusAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = _directoryPath,
                ["search"] = Value(fields, "search"),
                ["sort"] = sort,
                ["descending"] = descending.ToString(CultureInfo.InvariantCulture),
                ["collision-policy"] = Value(fields, "collision-policy", "reject"),
                ["content"] = _selectedTextFile?.Content ?? string.Empty,
                ["encoding"] = _selectedTextFile?.Encoding ?? "utf-8",
                ["entry-name-encoding"] = Value(fields, "entry-name-encoding", "utf-8"),
                ["extract-encoding"] = Value(fields, "extract-encoding", "auto"),
                ["overwrite"] = Value(fields, "overwrite", "false"),
            },
            ("directory-tabs", _directoryTabs.Select((tabPath, index) => Item(
                tabPath.Length == 0 ? "/" : tabPath,
                index == _activeDirectoryTab ? services.Localization.Get("state.current") : null,
                new DirectoryTab(index, tabPath, index == _activeDirectoryTab))).ToArray(), null),
            ("breadcrumb", [Item(_directoryPath.Length == 0 ? "/" : _directoryPath, FormatBytes(status.AvailableBytes), status)], null),
            ("file-list", Items(page.Items, item => item.Path, item => item.IsDirectory ? services.Localization.Get("common.directory") : FormatBytes(item.Size)), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadConfigurationsAsync(CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        CursorPage<ConfigurationDto> page = await services.Session.Client.ListConfigurationsAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal),
            ("configuration-list", Items(page.Items, item => item.RelativePath, item => item.Format), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadConfigurationEditorAsync(CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        ConfigurationDto configuration = _selectedConfiguration
            ?? throw new InvalidOperationException(services.Localization.Get("common.not-selected"));
        ConfigurationDetailsDto details = await services.Session.Client.GetConfigurationAsync(
            instance.Id, instance.NodeId, configuration.Key, cancellationToken).ConfigureAwait(false);
        ConfigurationDetails = details;
        ConfigurationSchema = details.Format == "properties"
            ? await services.Session.Client.GetConfigurationSchemaAsync(
                instance.Id, instance.NodeId, configuration.Key, cancellationToken).ConfigureAwait(false)
            : [];
        if (ConfigurationSchema.Count == 0)
        {
            ConfigurationRawMode = true;
        }
        _configurationRevision = details.Revision;
        CursorPage<ConfigurationBackupDto> backups = await services.Session.Client.ListConfigurationBackupsAsync(
            instance.Id, instance.NodeId, configuration.Key, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content"] = details.RawText },
            ("validation", (details.Diagnostics ?? []).Select(value => Item(value, null, value)).ToArray(), null),
            ("backups", Items(backups.Items, item => item.CreatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture), item => FormatBytes(item.Size)), backups.NextCursor));
    }

    private async Task<PageSnapshot> LoadModsAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        string kind = Value(fields, "type", "mods");
        CursorPage<ModDetailsDto> page = await services.Session.Client.ListModsAsync(
            instance.Id, instance.NodeId, kind, Value(fields, "query"), null, 50, cancellationToken).ConfigureAwait(false);
        bool? enabled = bool.TryParse(Value(fields, "enabled"), out bool parsedEnabled) ? parsedEnabled : null;
        IReadOnlyList<ModDetailsDto> items = enabled is null ? page.Items : [.. page.Items.Where(item => item.Enabled == enabled)];
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = Value(fields, "query"),
                ["type"] = kind,
                ["enabled"] = Value(fields, "enabled"),
            },
            ("mod-list", Items(items, item => item.Name, item => (item.Version ?? string.Empty) + " · " + services.Localization.Get(item.Enabled ? "state.enabled" : "state.disabled")), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadSchedulesAsync(CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        CursorPage<ScheduledTaskDetailsDto> tasks = await services.Session.Client.ListSchedulesAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        var selected = _selected as ScheduledTaskDetailsDto;
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["name"] = selected?.Name ?? string.Empty,
            ["trigger"] = selected is null ? "interval:300" : FormatScheduleTrigger(selected.Trigger),
            ["actions"] = selected is null ? "command:say Servitium" : string.Join(Environment.NewLine, selected.Actions.Select(value => value.Kind + ":" + value.Argument)),
            ["remaining-runs"] = (selected?.RemainingRuns ?? -1).ToString(CultureInfo.InvariantCulture),
            ["overlap-policy"] = selected?.OverlapPolicy ?? "skip",
        };
        return SnapshotPaged(fields, ("task-list", Items(tasks.Items, item => item.Name, item => services.Localization.Get(item.Enabled ? "state.enabled" : "state.disabled")), tasks.NextCursor));
    }

    private async Task<PageSnapshot> LoadInstanceSettingsAsync(CancellationToken cancellationToken)
    {
        InstanceDetailsDto details = await services.Session.Client.GetInstanceAsync(RequireInstance().Id, cancellationToken).ConfigureAwait(false);
        UpdateInstanceRequest? configuration = details.Configuration;
        UpdateInstanceRequest current = configuration ?? DefaultInstanceConfiguration(details);
        TerminalSettingsRequest terminal = current.Terminal ?? new TerminalSettingsRequest();
        UpdateActionRequest update = current.UpdateAction ?? new UpdateActionRequest();
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["name"] = details.Instance.Name,
            ["tags"] = string.Join(", ", details.Instance.Tags),
            ["notes"] = details.Notes ?? string.Empty,
            ["working-directory"] = details.Instance.WorkingDirectory,
            ["java-runtime"] = details.Launch.JavaRuntimeId.ToString("D"),
            ["minimum-heap"] = details.Launch.MinimumHeapMegabytes.ToString(CultureInfo.InvariantCulture),
            ["maximum-heap"] = details.Launch.MaximumHeapMegabytes.ToString(CultureInfo.InvariantCulture),
            ["jvm-arguments"] = string.Join(Environment.NewLine, details.Launch.JvmArguments),
            ["target-kind"] = details.Launch.TargetKind,
            ["launch"] = details.Launch.Target,
            ["server-arguments"] = string.Join(Environment.NewLine, details.Launch.ServerArguments),
            ["environment-variables"] = string.Join(Environment.NewLine, (details.Launch.EnvironmentVariables ?? new Dictionary<string, string>()).Select(pair => pair.Key + "=" + pair.Value)),
            ["advanced-mode"] = details.Launch.AdvancedMode.ToString(CultureInfo.InvariantCulture),
            ["advanced-arguments"] = string.Join(Environment.NewLine, details.Launch.AdvancedArguments ?? []),
            ["run-credential-alias"] = current.RunCredentialAlias ?? string.Empty,
            ["stop-command"] = current.StopCommand,
            ["stop-timeout"] = current.StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["primary-port"] = current.PrimaryPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["auto-start"] = current.AutoStart.ToString(CultureInfo.InvariantCulture),
            ["auto-start-delay"] = current.AutoStartDelaySeconds.ToString(CultureInfo.InvariantCulture),
            ["auto-restart"] = current.AutoRestart.ToString(CultureInfo.InvariantCulture),
            ["maximum-restart-attempts"] = current.MaximumRestartAttempts.ToString(CultureInfo.InvariantCulture),
            ["restart-interval"] = current.RestartIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            ["restart-reset"] = current.RestartResetSeconds.ToString(CultureInfo.InvariantCulture),
            ["terminal-conpty"] = terminal.UseConPty.ToString(CultureInfo.InvariantCulture),
            ["terminal-columns"] = terminal.Columns.ToString(CultureInfo.InvariantCulture),
            ["terminal-rows"] = terminal.Rows.ToString(CultureInfo.InvariantCulture),
            ["terminal-input-encoding"] = terminal.InputEncoding,
            ["terminal-output-encoding"] = terminal.OutputEncoding,
            ["terminal-newline"] = terminal.NewLine,
            ["terminal-ansi"] = terminal.AnsiEnabled.ToString(CultureInfo.InvariantCulture),
            ["rcon-enabled"] = current.Rcon.Enabled.ToString(CultureInfo.InvariantCulture),
            ["rcon-port"] = current.Rcon.Port.ToString(CultureInfo.InvariantCulture),
            ["rcon-password"] = string.Empty,
            ["query-enabled"] = current.Query.Enabled.ToString(CultureInfo.InvariantCulture),
            ["query-host"] = current.Query.Host,
            ["query-port"] = current.Query.Port.ToString(CultureInfo.InvariantCulture),
            ["update-kind"] = update.Kind,
            ["update-source"] = update.SourceRelativePath ?? string.Empty,
            ["update-target"] = update.TargetRelativePath ?? string.Empty,
            ["update-channel"] = update.ChannelId?.ToString("D") ?? string.Empty,
            ["shortcut-commands"] = string.Join(Environment.NewLine, (current.ShortcutCommands ?? []).Select(command => command.Name + "=" + command.Command)),
        };
        return Snapshot(fields, ("summary", [Item(details.Instance.Name, DisplayValue("status", details.Instance.Status), details)]));
    }

    private async Task<PageSnapshot> LoadInstanceCreationAsync(
        IReadOnlyDictionary<string, string> currentFields,
        CancellationToken cancellationToken)
    {
        await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
        if (Guid.TryParse(Value(currentFields, "node"), out Guid requestedNodeId))
        {
            services.Session.SelectedNode = services.Session.Nodes.FirstOrDefault(node => node.Id == requestedNodeId) ?? services.Session.SelectedNode;
        }

        IReadOnlyList<JavaRuntimeDto> java = services.Session.SelectedNode is null
            ? []
            : (await services.Session.Client.ListJavaRuntimesAsync(services.Session.SelectedNode.Id, cancellationToken).ConfigureAwait(false)).Items;
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["source-kind"] = Value(currentFields, "source-kind", "empty"),
            ["name"] = Value(currentFields, "name"),
            ["tags"] = Value(currentFields, "tags"),
            ["notes"] = Value(currentFields, "notes"),
            ["node"] = services.Session.SelectedNode?.Id.ToString("D") ?? string.Empty,
            ["absolute-path"] = Value(currentFields, "absolute-path"),
            ["archive"] = Value(currentFields, "archive"),
            ["java-runtime"] = Value(currentFields, "java-runtime", java.Count > 0 ? java[0].Id.ToString("D") : string.Empty),
            ["minimum-heap"] = Value(currentFields, "minimum-heap", "1024"),
            ["maximum-heap"] = Value(currentFields, "maximum-heap", "2048"),
            ["jvm-arguments"] = Value(currentFields, "jvm-arguments"),
            ["target-kind"] = Value(currentFields, "target-kind", "jar"),
            ["launch"] = Value(currentFields, "launch", "server.jar"),
            ["server-arguments"] = Value(currentFields, "server-arguments"),
            ["environment-variables"] = Value(currentFields, "environment-variables"),
            ["advanced-mode"] = Value(currentFields, "advanced-mode", "false"),
            ["advanced-arguments"] = Value(currentFields, "advanced-arguments"),
            ["stop-command"] = Value(currentFields, "stop-command", "stop"),
            ["stop-timeout"] = Value(currentFields, "stop-timeout", "60"),
        };
        return Snapshot(
            fields,
            ("node", Items(services.Session.Nodes, item => item.Name, item => DisplayValue("status", item.Status))),
            ("launch", Items(java, item => item.DisplayName ?? item.Version, item => item.ExecutablePath)),
            ("review", Items(services.Session.Channels, item => item.Name, item => DisplayValue("visibility", item.Visibility))));
    }

    private async Task<PageSnapshot> LoadNodesAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        CursorPage<Contracts.Nodes.NodeDto> page = await services.Session.Client.ListNodesAsync(
            Value(fields, "status"), Value(fields, "query"), null, 100, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = Value(fields, "query"),
                ["status"] = Value(fields, "status"),
                ["registration-name"] = Value(fields, "registration-name"),
                ["registration-lifetime"] = Value(fields, "registration-lifetime", "30"),
            },
            ("node-list", Items(page.Items, item => item.Name, item => DisplayValue("status", item.Status)), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadNodeDetailsAsync(CancellationToken cancellationToken)
    {
        Contracts.Nodes.NodeDto node = RequireNode();
        Contracts.Nodes.NodeDetailsDto details = await services.Session.Client.GetNodeAsync(node.Id, cancellationToken).ConfigureAwait(false);
        CursorPage<Contracts.Nodes.NodeMetricsDto> metrics = await services.Session.Client.ListNodeMetricsAsync(node.Id, null, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = details.Name,
                ["notes"] = details.Configuration.Notes ?? string.Empty,
                ["tags"] = string.Join(", ", details.Configuration.Tags ?? []),
                ["public-address"] = details.Configuration.PublicAddress ?? string.Empty,
                ["address-mappings"] = string.Join(Environment.NewLine, (details.Configuration.AddressMappings ?? []).Select(mapping => mapping.InternalAddress + "=" + mapping.PublicAddress)),
                ["default-instance-path"] = details.Configuration.DefaultInstancePath ?? string.Empty,
                ["java-data-directory"] = details.Configuration.JavaDataDirectory ?? string.Empty,
                ["upload-rate"] = details.Configuration.UploadBytesPerSecond.ToString(CultureInfo.InvariantCulture),
                ["download-rate"] = details.Configuration.DownloadBytesPerSecond.ToString(CultureInfo.InvariantCulture),
                ["url-download-concurrency"] = details.Configuration.UrlDownloadConcurrency.ToString(CultureInfo.InvariantCulture),
                ["file-task-concurrency"] = details.Configuration.FileTaskConcurrency.ToString(CultureInfo.InvariantCulture),
                ["compression-task-concurrency"] = details.Configuration.CompressionTaskConcurrency.ToString(CultureInfo.InvariantCulture),
                ["terminal-buffer-bytes"] = details.Configuration.TerminalBufferBytes.ToString(CultureInfo.InvariantCulture),
                ["terminal-buffer-lines"] = details.Configuration.TerminalBufferLines.ToString(CultureInfo.InvariantCulture),
                ["graceful-shutdown"] = details.Configuration.GracefulShutdownEnabled.ToString(CultureInfo.InvariantCulture),
                ["shutdown-wait"] = details.Configuration.ShutdownWaitSeconds.ToString(CultureInfo.InvariantCulture),
                ["metric-interval"] = details.Configuration.MetricIntervalSeconds.ToString(CultureInfo.InvariantCulture),
                ["maximum-schedules"] = details.Configuration.MaximumSchedulesPerInstance.ToString(CultureInfo.InvariantCulture),
                ["advanced-raw-commands"] = details.Configuration.AdvancedRawCommandsEnabled.ToString(CultureInfo.InvariantCulture),
                ["private-source-authorities"] = string.Join(Environment.NewLine, details.Configuration.PrivateSourceAuthorities ?? []),
            },
            ("summary", [Item(details.Name, DisplayValue("status", details.Status) + " · " + details.OperatingSystem, details)], null),
            ("metrics", metrics.Items.TakeLast(60).Select(item => Item(item.SampledAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture), item.CpuPercent.ToString("F1", CultureInfo.CurrentCulture) + "% · " + FormatBytes(item.MemoryUsedBytes), item)).ToArray(), metrics.NextCursor));
    }

    private async Task<PageSnapshot> LoadJavaAsync(CancellationToken cancellationToken)
    {
        Contracts.Nodes.NodeDto node = RequireNode();
        CursorPage<JavaRuntimeDto> page = await services.Session.Client.ListJavaRuntimesAsync(node.Id, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal),
            ("runtime-list", Items(page.Items, item => item.DisplayName ?? item.Version, item => item.ExecutablePath), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadUsersAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        string role = Value(fields, "role", "User");
        CursorPage<UserDto> users = await services.Session.Client.ListUsersAsync(role, Value(fields, "query"), null, 100, cancellationToken).ConfigureAwait(false);
        var selected = _selected as UserDto;
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = Value(fields, "query"),
                ["role"] = selected?.Role ?? role,
                ["user-name"] = selected?.UserName ?? string.Empty,
                ["display-name"] = selected?.DisplayName ?? string.Empty,
                ["password"] = string.Empty,
                ["windows-sid"] = string.Empty,
            },
            ("user-list", Items(users.Items, item => item.DisplayName, item => item.UserName + " · " + DisplayValue("role", item.Role)), users.NextCursor));
    }

    private async Task<PageSnapshot> LoadGrantsAsync(
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        UserDto user = RequireUser();
        InstanceGrantsDto grants = await services.Session.Client.GetUserGrantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        _currentGrantIds = [.. grants.InstanceIds];
        await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
        string query = Value(fields, "query");
        string grantState = Value(fields, "grant-state", "all");
        IEnumerable<InstanceDto> instances = services.Session.Instances
            .Where(instance => query.Length == 0 || instance.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Where(instance => grantState switch
            {
                "granted" => _currentGrantIds.Contains(instance.Id),
                "not-granted" => !_currentGrantIds.Contains(instance.Id),
                _ => true,
            })
            .OrderBy(instance => instance.NodeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(instance => instance.Name, StringComparer.CurrentCultureIgnoreCase);
        return Snapshot(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = query,
                ["grant-state"] = grantState,
            },
            ("grants", Items(
                instances,
                item => item.Name,
                item => (item.NodeName ?? services.Localization.Get("common.unknown")) + " · " +
                    services.Localization.Get(_currentGrantIds.Contains(item.Id) ? "state.granted" : "state.not-granted"))));
    }

    private async Task<PageSnapshot> LoadAccountAsync(CancellationToken cancellationToken)
    {
        PrincipalDto principal = await services.Session.Client.Session.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        CursorPage<SessionDto> sessions = await services.Session.Client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        CursorPage<ApiKeyDto> keys = await services.Session.Client.ListApiKeysAsync(cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["display-name"] = principal.User.DisplayName,
                ["language"] = principal.Language,
            },
            ("api-keys", Items(keys.Items, item => item.Name, item => item.Scope), keys.NextCursor),
            ("sessions", Items(sessions.Items, item => item.CreatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture), item => item.Current ? services.Localization.Get("state.current") : item.ExpiresAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), sessions.NextCursor));
    }

    private async Task<PageSnapshot> LoadDashboardAsync(CancellationToken cancellationToken)
    {
        Contracts.Governance.DashboardOverviewDto dashboard = await services.Session.Client.GetDashboardAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OperationDetailsDto> operations = (await services.Session.Client.ListOperationsAsync(null, null, null, null, cancellationToken).ConfigureAwait(false)).Items;
        DashboardLayoutDto layout = await services.Session.Client.GetLayoutAsync("dashboard", cancellationToken).ConfigureAwait(false);
        Dictionary<string, PageSectionSnapshot> sections = new(StringComparer.Ordinal)
        {
            ["node-status"] = new([Item(
                dashboard.OnlineNodes.ToString(CultureInfo.CurrentCulture) + "/" + dashboard.TotalNodes.ToString(CultureInfo.CurrentCulture),
                services.Localization.Get("common.nodes"), dashboard)]),
            ["instance-status"] = new([Item(
                dashboard.RunningInstances.ToString(CultureInfo.CurrentCulture) + "/" + dashboard.TotalInstances.ToString(CultureInfo.CurrentCulture),
                services.Localization.Get("common.instances"), dashboard)]),
            ["session-status"] = new([Item(
                dashboard.ActiveSessions.ToString(CultureInfo.CurrentCulture), services.Localization.Get("common.sessions"), dashboard)]),
            ["system-status"] = new([Item(
                dashboard.ControlPlaneCpuPercent.ToString("F1", CultureInfo.CurrentCulture) + "%",
                FormatBytes(dashboard.ControlPlaneMemoryBytes), dashboard)]),
            ["system-info"] = new([
                Item(services.Localization.Get("app.window-title"), Environment.OSVersion.VersionString, dashboard),
                Item(".NET " + Environment.Version.ToString(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), dashboard)]),
            ["activity"] = new(Items(operations.Take(10), item => DisplayValue("operation-kind", item.Operation.Kind), item => DisplayValue("status", item.Operation.Status))),
            ["node-overview"] = new(Items(dashboard.Nodes, item => item.Name, item => DisplayValue("status", item.Status) + " · " + item.InstanceCount.ToString(CultureInfo.CurrentCulture))),
            ["request-chart"] = new(Items(dashboard.Trend, item => item.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), item => item.RequestCount.ToString(CultureInfo.CurrentCulture))),
            ["instance-chart"] = new(Items(dashboard.Trend, item => item.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), item => item.InstanceCount.ToString(CultureInfo.CurrentCulture))),
        };
        return new PageSnapshot(sections, new Dictionary<string, string>(StringComparer.Ordinal), DashboardLayout: layout);
    }

    private async Task<PageSnapshot> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        SettingsDto settings = await services.Session.Client.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (SettingsGroupDto group in settings.Groups)
        {
            foreach ((string key, string? value) in group.Values)
            {
                _ = fields.TryAdd(key, value ?? string.Empty);
            }
        }

        return Snapshot(fields, ("general", Items(
            settings.Groups,
            item => item.Group,
            item => services.Localization.Format("settings.revision", item.Revision))));
    }

    private async Task<PageSnapshot> LoadActivityAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        CursorPage<ActivityLogDto> page = await services.Session.Client.ListActivityLogsAsync(
            ActivityFilter(fields), cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(new Dictionary<string, string>(fields, StringComparer.Ordinal), ("log-list", Items(page.Items, item => item.Action, item => item.ActorName + " · " + item.OccurredAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadAuditAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        CursorPage<AuditLogDto> page = await services.Session.Client.ListSecurityAuditAsync(
            SecurityFilter(fields), cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(new Dictionary<string, string>(fields, StringComparer.Ordinal), ("audit-list", Items(page.Items, item => item.Action, item => DisplayValue("audit-category", item.Category) + " · " + DisplayValue("outcome", item.OutcomeCode)), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadOperationsAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        CursorPage<OperationDetailsDto> page = await services.Session.Client.ListOperationsAsync(
            Value(fields, "status"), Value(fields, "kind"), null, null, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(new Dictionary<string, string>(fields, StringComparer.Ordinal), ("operation-list", Items(page.Items, item => DisplayValue("operation-kind", item.Operation.Kind), item => DisplayValue("status", item.Operation.Status) + " · " + item.Target), page.NextCursor));
    }

    private async Task<PageSnapshot> LoadDistributionAsync(CancellationToken cancellationToken)
    {
        await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
        HmclReleaseDto[] activeReleases = await Task.WhenAll(services.Session.Channels
            .Where(channel => channel.ActiveReleaseId is not null)
            .Take(10)
            .Select(channel => services.Session.Client.GetHmclReleaseAsync(channel.ActiveReleaseId!.Value, cancellationToken))).ConfigureAwait(false);
        return Snapshot(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["visibility"] = "public" },
            ("channels", Items(services.Session.Channels, item => item.Name, item => DisplayValue("visibility", item.Visibility))),
            ("recent-releases", Items(activeReleases.OrderByDescending(item => item.CreatedAt), item => item.Name + " " + item.Version, item => item.CreatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture))));
    }

    private async Task<PageSnapshot> LoadChannelAsync(CancellationToken cancellationToken)
    {
        HmclChannelDto channel = RequireChannel();
        HmclChannelDetailsDto details = await services.Session.Client.GetHmclChannelAsync(channel.Id, cancellationToken).ConfigureAwait(false);
        CursorPage<HmclReleaseSummaryDto> releases = await services.Session.Client.ListHmclReleasesAsync(channel.Id, cancellationToken).ConfigureAwait(false);
        CursorPage<HmclAccessStatDto> stats = await services.Session.Client.ListHmclStatsAsync(channel.Id, 30, cancellationToken).ConfigureAwait(false);
        HmclReleaseSummaryDto? active = releases.Items.FirstOrDefault(item => item.IsActive);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = details.Channel.Name,
                ["description"] = details.Description,
                ["retention-count"] = details.RetentionCount.ToString(CultureInfo.InvariantCulture),
                ["linked-instance"] = details.LinkedInstanceId?.ToString("D") ?? string.Empty,
                ["visibility"] = details.Channel.Visibility,
                ["access-token"] = string.Empty,
            },
            ("summary", [Item(details.Channel.Name, details.ManifestUrl.AbsoluteUri, details)], null),
            ("active-release", active is null ? [] : [Item(active.Name + " " + active.Version, active.GameVersion, active)], null),
            ("statistics", stats.Items.Select(item => Item(
                item.Date.ToString("d", CultureInfo.CurrentCulture),
                (item.Manifest200 + item.Manifest304).ToString(CultureInfo.CurrentCulture) + " · " + FormatBytes(item.Bytes),
                item)).ToArray(), stats.NextCursor),
            ("settings", [Item(DisplayValue("integrity", details.IntegrityStatus), details.LinkedInstanceId?.ToString("D"), details)], null),
            ("release-history", Items(releases.Items, item => item.Name + " " + item.Version, item => item.IsActive ? services.Localization.Get("state.active") : item.CreatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), releases.NextCursor));
    }

    private async Task<PageSnapshot> LoadReleaseCreationAsync(
        IReadOnlyDictionary<string, string> currentFields,
        CancellationToken cancellationToken)
    {
        await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> fields = new(currentFields, StringComparer.Ordinal);
        _ = fields.TryAdd("source-kind", "archive");
        _ = fields.TryAdd("author", services.Session.Principal?.User.DisplayName ?? string.Empty);
        _ = fields.TryAdd("version", "1.0.0");
        _ = fields.TryAdd("game-version", "1.21.1");
        StagedPrismPreviewDto? staged = _stagedPrismPreview;
        if (staged is not null)
        {
            fields["source-kind"] = "prism";
            fields["name"] = Value(fields, "name", staged.Preview.InstanceName);
            fields["game-version"] = staged.Preview.GameVersion;
            (string? loaderId, string? loaderVersion) = ParseLoader(staged.Preview.Loader);
            fields["loader-id"] = loaderId ?? string.Empty;
            fields["loader-version"] = loaderVersion ?? string.Empty;
        }

        List<PageListItem> inspection = staged is null
            ? []
            : [Item(
                services.Localization.Get("hmcl.preview.archive-safe"),
                services.Localization.Format("hmcl.preview.candidate-count", staged.Preview.IncludedPaths.Count),
                staged)];
        IReadOnlyList<PageListItem> candidates = staged is null
            ? []
            : staged.Preview.IncludedPaths.Select(path => Item(path, services.Localization.Get("hmcl.preview.included"), staged)).ToArray();
        IReadOnlyList<PageListItem> excluded = staged is null
            ? []
            : staged.Preview.ExcludedPaths.Select(pair => Item(pair.Key, services.Localization.Get("hmcl.exclusion." + pair.Value), staged)).ToArray();
        IReadOnlyList<PageListItem> difference = staged?.Difference is null
            ? []
            : DifferenceItems(staged.Difference, staged);
        return Snapshot(
            fields,
            ("source", Items(services.Session.Instances, item => item.Name, item => DisplayValue("status", item.Status))),
            ("upload", candidates),
            ("inspection", inspection),
            ("filter-preview", excluded),
            ("difference-preview", difference));
    }

    private async Task<PageSnapshot> LoadReleaseAsync(CancellationToken cancellationToken)
    {
        HmclReleaseSummaryDto summary = services.Session.SelectedRelease
            ?? throw new InvalidOperationException(services.Localization.Get("common.not-selected"));
        HmclReleaseDto release = await services.Session.Client.GetHmclReleaseAsync(summary.Id, cancellationToken).ConfigureAwait(false);
        HmclReleaseFilePageDto files = await services.Session.Client.ListHmclReleaseFilesAsync(release.Id, null, null, 200, cancellationToken).ConfigureAwait(false);
        return SnapshotPaged(
            new Dictionary<string, string>(StringComparer.Ordinal),
            ("metadata", [Item(release.Name + " " + release.Version, release.GameVersion, release)], null),
            ("files", Items(files.Items, item => item.Path, item => FormatBytes(item.Size)), files.NextCursor),
            ("usage-history", Items(
                release.Activations ?? [],
                item => item.StartedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture),
                item => item.EndedAt?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? services.Localization.Get("state.active")),
                null));
    }

    private async Task<PageSnapshot> LoadPublicAsync(CancellationToken cancellationToken)
    {
        if (services.Session.Principal is null)
        {
            return Snapshot(new Dictionary<string, string>(StringComparer.Ordinal), ("branding", [Item(services.Localization.Get("app.window-title"), services.Localization.Get("app.description"), services)]));
        }

        await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
        return Snapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            ("branding", [Item(services.Localization.Get("app.window-title"), services.Localization.Get("app.description"), services)]),
            ("channels", Items(services.Session.Channels.Where(item => item.Visibility == "public"), item => item.Name, item => item.ManifestUrl?.AbsoluteUri)));
    }

    private static PageSnapshot Empty(string? statusKey = null)
    {
        return new(new Dictionary<string, PageSectionSnapshot>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal), statusKey);
    }

    private static PageSnapshot Snapshot(
        IReadOnlyDictionary<string, string> fields,
        params (string Section, IReadOnlyList<PageListItem> Items)[] sections)
    {
        Dictionary<string, PageSectionSnapshot> result = new(StringComparer.Ordinal);
        foreach ((string section, IReadOnlyList<PageListItem> items) in sections)
        {
            result[section] = new PageSectionSnapshot(items);
        }

        return new PageSnapshot(result, fields);
    }

    private static PageSnapshot SnapshotPaged(
        IReadOnlyDictionary<string, string> fields,
        params (string Section, IReadOnlyList<PageListItem> Items, string? NextCursor)[] sections)
    {
        Dictionary<string, PageSectionSnapshot> result = new(StringComparer.Ordinal);
        foreach ((string section, IReadOnlyList<PageListItem> items, string? nextCursor) in sections)
        {
            result[section] = new PageSectionSnapshot(items, nextCursor);
        }

        return new PageSnapshot(result, fields);
    }

    private static PageListItem Item(string primary, string? secondary, object value)
    {
        return new(primary, secondary, value);
    }

    private static IReadOnlyList<PageListItem> Items<T>(
        IEnumerable<T> values,
        Func<T, string> primary,
        Func<T, string?> secondary)
    {
        return [.. values.Select(value => Item(primary(value), secondary(value), value!))];
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double display = value;
        int unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return display.ToString(unit == 0 ? "F0" : "F1", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}

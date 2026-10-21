using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Nodes;
using Servitium.Desktop.Services;
using Servitium.Client.Http;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceWorkspaceViewModel(
    LocalizationService localization,
    ServitiumApiClient client,
    bool administrator,
    Func<string, string, Task<bool>> confirm,
    Action<InstanceDto?> select) : DesktopViewModel(localization)
{
    private IReadOnlyList<InstanceGroupDto> _source = [];
    private Guid? _selectedId;
    public bool IsAdministrator => administrator;
    public IReadOnlyList<InstanceNodeGroup> Groups { get; private set => SetProperty(ref field, value); } = [];
    public InstanceRow? Selected { get; private set => SetProperty(ref field, value); }
    public InstanceDetailsDto? Details { get; private set => SetProperty(ref field, value); }
    public string Summary { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string Count { get; private set => SetProperty(ref field, value); } = string.Empty;
    public bool IsEmpty { get; private set => SetProperty(ref field, value); } = true;
    public bool HasSelection => Selected is not null;
    public NodeDto? SelectedNode => _source.FirstOrDefault(group => group.Node.Id == Selected?.Value.NodeId)?.Node;
    public string Name => Selected?.Name ?? Text("desktop.select-instance");
    public string Subtitle => Selected is null ? Text("desktop.select-instance-next") :
        string.Join(" · ", new[] { Selected.Version, Selected.Value.NodeName ?? Text("desktop.node-not-reported"), Uptime() }.Where(value => value.Length > 0));
    public string Status => Selected?.Status ?? string.Empty;
    public string StateMessage => Selected is null ? Text("desktop.select-instance-next") : !Selected.IsReachable
        ? Text("desktop.instance-offline-next") : Selected.State is "Crashed" or "Failed"
            ? Text("desktop.instance-crashed-next") : string.Empty;
    public bool HasStateMessage => StateMessage.Length > 0;
    public string Filter { get; set { if (SetProperty(ref field, value)) { ApplyFilter(); } } } = string.Empty;
    public int StatusFilter { get; set { if (SetProperty(ref field, value)) { ApplyFilter(); } } }
    public bool CanStart => CanInteract && Selected is { IsReachable: true, State: "Stopped" or "Crashed" or "Failed" };
    public bool CanStop => CanInteract && Selected is { IsReachable: true, State: "Running" };
    public bool CanForce => administrator && CanInteract && Selected is { IsReachable: true, State: "Running" or "Starting" or "Stopping" };
    public bool CanExportLog => CanInteract && Selected is { IsReachable: true };
    public bool CanUpdate => administrator && CanStart && Details?.Configuration?.UpdateAction?.Kind is "jar-replace" or "hmcl-channel-sync";

    public Task LoadAsync(Guid? selectedId = null)
    {
        _selectedId = selectedId ?? _selectedId;
        return RunAsync(async token =>
        {
            InstanceGroupsDto result = await client.ListGroupedInstancesAsync(token).ConfigureAwait(true);
            List<InstanceGroupDto> complete = [];
            foreach (InstanceGroupDto group in result.Groups)
            {
                List<InstanceDto> items = [.. group.Items];
                string? cursor = group.NextCursor;
                while (cursor is not null)
                {
                    InstanceGroupsDto next = await client.ListGroupedInstancesAsync(group.Node.Id, cursor, 200, token).ConfigureAwait(true);
                    InstanceGroupDto? page = next.Groups.FirstOrDefault(value => value.Node.Id == group.Node.Id);
                    if (page is null) { break; }
                    items.AddRange(page.Items); cursor = page.NextCursor;
                }
                complete.Add(group with { Items = items, NextCursor = null });
            }
            ApplySnapshot(complete);
            foreach (InstanceDto instance in complete.SelectMany(group => group.Items))
            {
                await client.Events.SubscribeAsync("instance:" + instance.Id.ToString("D"), token).ConfigureAwait(true);
            }
            if (_selectedId is Guid id) { await SelectCoreAsync(id, token).ConfigureAwait(true); }
        });
    }

    internal void ApplySnapshot(IReadOnlyList<InstanceGroupDto> groups)
    {
        groups = administrator ? groups : [.. groups.Where(group => group.Items.Count > 0)];
        _source = groups;
        InstanceDto[] instances = [.. groups.SelectMany(group => group.Items)];
        Count = Format("desktop.instance-count", instances.Length);
        Summary = Format("desktop.instance-summary", instances.Count(value => value.Status == "Running"),
            instances.Count(value => value.Status == "Stopped"), groups.Sum(group => group.Items.Count(value =>
                group.Node.Status != "Online" || value.Status is "Crashed" or "Failed" or "Unknown")),
            groups.Count(group => group.Node.Status == "Online"), groups.Count);
        ApplyFilter();
        Selected = Groups.SelectMany(group => group.Items).FirstOrDefault(row => row.Value.Id == _selectedId);
        if (Selected is null) { _selectedId = null; Details = null; select(null); }
        NotifySelection();
    }

    private void ApplyFilter()
    {
        Groups = [.. _source.Select(group => new InstanceNodeGroup(group.Node.Name,
            NodeDetail(group), group.Node.Status == "Online", [.. group.Items.Select(value => new InstanceRow(value, Localization, group.Node.Status == "Online"))
                .Where(row => row.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase) && (StatusFilter switch
                { 1 => row.State == "Running", 2 => row.State == "Stopped", 3 => row.NeedsAttention, _ => true }))]))
            .Where(group => group.Items.Count > 0 || Filter.Length == 0 && StatusFilter == 0)];
        IsEmpty = Groups.All(group => group.Items.Count == 0);
    }

    private string NodeDetail(InstanceGroupDto group)
    {
        string address = group.Node.PublicAddress ?? Text("desktop.node-address-not-reported");
        return group.Node.Status == "Online" ? address + " · " + Text("state.online") : address + " · " +
            (group.Node.LastSeenAt is DateTimeOffset last ? OfflineDuration(last) : Text("desktop.node-never-seen"));
    }

    private string OfflineDuration(DateTimeOffset last)
    {
        TimeSpan gap = client.TimeProvider.GetUtcNow() - last;
        return Format("desktop.node-offline-duration", (long)Math.Max(0, gap.TotalHours), Math.Max(0, gap.Minutes), last.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
    }

    public Task SelectAsync(InstanceRow row)
    {
        return RunAsync(token => SelectCoreAsync(row.Value.Id, token));
    }
    public void ShowPermissionDenied()
    {
        Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
        Message = Text("desktop.permission-error");
    }

    private async Task SelectCoreAsync(Guid id, CancellationToken token)
    {
        Selected = Groups.SelectMany(group => group.Items).FirstOrDefault(row => row.Value.Id == id);
        if (Selected is null) { return; }
        _selectedId = id; select(Selected.Value); NotifySelection();
        Details = await client.GetInstanceAsync(id, token).ConfigureAwait(true);
        OnPropertyChanged(nameof(CanUpdate)); UpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() { return LifecycleAsync(async (instance, token) => await client.StartInstanceAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true)); }
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() { return LifecycleAsync(async (instance, token) => await client.StopInstanceAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true)); }
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task RestartAsync() { return LifecycleAsync(async (instance, token) => await client.RestartInstanceAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true)); }
    [RelayCommand(CanExecute = nameof(CanForce))]
    private Task ForceStopAsync()
    {
        return LifecycleAsync(async (instance, token) =>
        {
            return !await confirm("desktop.force-stop-title", Text("desktop.force-stop-impact")).ConfigureAwait(true)
                ? null
                : await client.ForceStopInstanceAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true);
        });
    }
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateAsync()
    {
        return LifecycleAsync(async (instance, token) =>
        {
            return !await confirm("desktop.instance-update-title", Text("desktop.instance-update-impact")).ConfigureAwait(true)
                ? null
                : await client.RunInstanceUpdateAsync(instance.Id, instance.NodeId, new RunInstanceUpdateRequest(true), token).ConfigureAwait(true);
        });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task AcceptEulaAsync()
    {
        return Selected is not { Value: var instance }
            ? Task.CompletedTask
            : RunAsync(async token =>
        {
            if (!await confirm("desktop.accept-eula", Text("desktop.accept-eula-impact")).ConfigureAwait(true)) { return; }
            await client.AcceptEulaAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true);
            Message = Text("desktop.eula-accepted");
        });
    }

    private Task LifecycleAsync(Func<InstanceDto, CancellationToken, Task<OperationAcceptedDto?>> execute)
    {
        InstanceDto? instance = Selected?.Value;
        return instance is null
            ? Task.CompletedTask
            : RunAsync(async token =>
        {
            OperationAcceptedDto? operation = await execute(instance, token).ConfigureAwait(true);
            if (operation is null) { return; }
            Message = Text("desktop.operation-queued");
            _ = await DesktopOperationService.WaitAsync(client, operation.OperationId, token).ConfigureAwait(true);
            InstanceRuntimeDto runtime = await client.GetInstanceStatusAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true);
            _source = [.. _source.Select(group => group with { Items = [.. group.Items.Select(value => value.Id == instance.Id ? value with { Status = runtime.Status, LastStartedAt = runtime.StartedAt } : value)] })];
            ApplySnapshot(_source);
            if (Selected is not null) { select(Selected.Value); }
            Message = Text("common.operation-completed");
        });
    }

    public Task ExportLogAsync(Func<Task<string?>> choosePath)
    {
        InstanceDto? instance = Selected?.Value;
        return instance is null
            ? Task.CompletedTask
            : RunAsync(async token =>
        {
            string? path = await choosePath().ConfigureAwait(true);
            if (path is null) { return; }
            DownloadedFile download = await client.ExportTerminalSessionLogAsync(instance.Id, instance.NodeId, false, token).ConfigureAwait(true);
            await System.IO.File.WriteAllBytesAsync(path, download.Content, token).ConfigureAwait(true);
            Message = Text("common.operation-completed");
        });
    }

    private string Uptime()
    {
        if (Selected?.Value is not { Status: "Running", LastStartedAt: DateTimeOffset started }) { return string.Empty; }
        TimeSpan duration = client.TimeProvider.GetUtcNow() - started;
        return Format("desktop.instance-uptime", (int)Math.Max(0, duration.TotalHours), Math.Max(0, duration.Minutes));
    }

    private void NotifySelection()
    {
        foreach (string name in new[] { nameof(HasSelection), nameof(Name), nameof(Subtitle), nameof(Status), nameof(StateMessage), nameof(HasStateMessage) }) { OnPropertyChanged(name); }
        RefreshCommands();
    }

    protected override void RefreshCommands()
    {
        foreach (string name in new[] { nameof(CanStart), nameof(CanStop), nameof(CanForce), nameof(CanUpdate), nameof(CanExportLog) }) { OnPropertyChanged(name); }
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged();
        ForceStopCommand.NotifyCanExecuteChanged(); UpdateCommand.NotifyCanExecuteChanged();
        AcceptEulaCommand.NotifyCanExecuteChanged();
    }
}

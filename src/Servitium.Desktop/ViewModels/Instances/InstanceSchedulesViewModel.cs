using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Scheduling;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceSchedulesViewModel : DesktopViewModel
{
    private readonly ServitiumApiClient _client;
    private readonly InstanceDto _instance;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly HashSet<ScheduleActionEditor> _subscribedActions = [];
    private bool _reachable;
    private bool _loading;
    private string _baseline = string.Empty;
    private Guid? _editingId;
    private long? _revision;

    internal InstanceSchedulesViewModel(LocalizationService localization, ServitiumApiClient client, InstanceDto instance,
        Func<string, string, Task<bool>> confirm, bool reachable = true) : base(localization)
    {
        _client = client; _instance = instance; _confirm = confirm; _reachable = reachable;
        Trigger = new(localization, client.TimeProvider); Trigger.PropertyChanged += OnEditorChanged;
        Actions.CollectionChanged += OnActionsChanged;
    }
    public IReadOnlyList<ScheduleRow> Schedules { get; private set => SetProperty(ref field, value); } = [];
    public ScheduleRow? SelectedTask { get; private set => SetProperty(ref field, value); }
    public ScheduleTriggerEditor Trigger { get; }
    public ObservableCollection<ScheduleActionEditor> Actions { get; } = [];
    public ScheduleActionEditor? SelectedAction { get; set { if (SetProperty(ref field, value)) { RefreshCommands(); } } }
    public string Name { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string RemainingRuns { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "-1";
    public int OverlapIndex { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public bool Enabled { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = true;
    public bool IsEditorOpen { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(EditorTitle)); Changed(); } } }
    public bool IsDirty => IsEditorOpen && Snapshot() != _baseline;
    public bool IsEmpty => Schedules.Count == 0;
    public bool IsOffline => !_reachable;
    public bool CanRead => CanInteract && _reachable;
    public bool CanOperate => CanRead && !IsDirty;
    public bool CanSave => CanRead && IsEditorOpen && IsDirty && !HasValidation;
    public bool CanCloseEditor => CanInteract && IsEditorOpen;
    public bool CanAddAction => CanRead && Actions.Count < 10;
    public bool CanRemoveAction => CanRead && Actions.Count > 1 && SelectedAction is not null;
    public bool CanMoveUp => CanRead && SelectedAction is not null && Actions.IndexOf(SelectedAction) > 0;
    public bool CanMoveDown => CanRead && SelectedAction is not null && Actions.IndexOf(SelectedAction) < Actions.Count - 1;
    public bool CanDelete => CanOperate && SelectedTask is not null;
    public bool CanViewHistory => CanRead && _editingId is not null;
    public string EditorTitle => Text(_editingId is null ? "desktop.schedule-new" : "desktop.schedule-edit");
    public string DirtyText => Text(IsDirty ? "desktop.schedule-unsaved" : "desktop.config-saved");
    public string Validation
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 64 || Name.Any(char.IsControl)) { return Text("desktop.schedule-name-invalid"); }
            if (Trigger.Validation.Length > 0) { return Trigger.Validation; }
            if (Actions.Count is < 1 or > 10 || Actions.Any(action => action.Validation.Length > 0)) { return Text("desktop.schedule-action-invalid"); }
            return !int.TryParse(RemainingRuns, NumberStyles.Integer, CultureInfo.InvariantCulture, out int remaining) || remaining is 0 or < -1 || OverlapIndex is < 0 or > 2
                ? Text("desktop.schedule-runs-invalid")
                : string.Empty;
        }
    }
    public bool HasValidation => Validation.Length > 0;
    public IReadOnlyList<ScheduleRunRow> History { get; private set => SetProperty(ref field, value); } = [];
    public bool NoHistory => History.Count == 0;
    public string Count => Format("desktop.schedules-shown", Schedules.Count);

    public Task LoadAsync() { return RunAsync(LoadCoreAsync); }
    private async Task LoadCoreAsync(CancellationToken token)
    {
        List<ScheduleRow> rows = []; string? cursor = null;
        do
        {
            CursorPage<ScheduledTaskDetailsDto> page = await _client.ListSchedulesAsync(_instance.Id, cursor, 200, token).ConfigureAwait(true);
            rows.AddRange(page.Items.Select(value => new ScheduleRow(value, Localization))); cursor = page.NextCursor;
        } while (cursor is not null);
        foreach (ScheduleRow row in rows)
        {
            IReadOnlyList<ScheduledTaskRunDto> runs = await ReadRunsAsync(row.Value.Id, token).ConfigureAwait(true);
            row.LastRun = runs.MaxBy(run => run.StartedAt);
        }
        Schedules = rows; SelectedTask = rows.FirstOrDefault(row => row.Value.Id == _editingId);
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Count)); RefreshCommands();
    }
    private async Task<IReadOnlyList<ScheduledTaskRunDto>> ReadRunsAsync(Guid taskId, CancellationToken token)
    {
        List<ScheduledTaskRunDto> runs = []; string? cursor = null;
        do
        {
            CursorPage<ScheduledTaskRunDto> page = await _client.ListScheduleRunsAsync(_instance.Id, taskId, cursor, 200, token).ConfigureAwait(true);
            runs.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        return runs;
    }
    public async Task OpenEditorAsync(ScheduleRow row)
    {
        if (!CanRead || _editingId == row.Value.Id && IsEditorOpen || !await CanLeaveAsync().ConfigureAwait(true)) { return; }
        await RunAsync(async token =>
        {
            ScheduledTaskDetailsDto details = await _client.GetScheduleAsync(_instance.Id, row.Value.Id, token).ConfigureAwait(true);
            row.Update(details); SelectedTask = row; Populate(details);
        }).ConfigureAwait(true);
    }
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task NewAsync()
    {
        if (!await CanLeaveAsync().ConfigureAwait(true)) { return; }
        SelectedTask = null; Populate(null); Message = string.Empty;
    }
    private void Populate(ScheduledTaskDetailsDto? task)
    {
        _loading = true;
        try
        {
            _editingId = task?.Id; _revision = task?.Revision; Name = task?.Name ?? string.Empty;
            Trigger.Load(task?.Trigger ?? new("interval", 300, null, null, null));
            RemainingRuns = (task?.RemainingRuns ?? -1).ToString(CultureInfo.InvariantCulture);
            OverlapIndex = task?.OverlapPolicy switch { "queue" => 1, "concurrent" => 2, _ => 0 }; Enabled = task?.Enabled ?? true;
            Actions.Clear(); foreach (ScheduleActionDto action in task?.Actions ?? [new("send-console-command", "list", false)]) { Actions.Add(new(Localization, action)); }
            SelectedAction = Actions.FirstOrDefault(); History = []; OnPropertyChanged(nameof(NoHistory));
            IsEditorOpen = true; _baseline = Snapshot(); OnPropertyChanged(nameof(EditorTitle));
        }
        finally { _loading = false; Changed(); }
    }
    private string Snapshot()
    {
        return JsonSerializer.Serialize(new { Name, Trigger = Trigger.Snapshot(), RemainingRuns, OverlapIndex, Enabled, Actions = Actions.Select(action => action.Request()).ToArray() });
    }
    private UpsertScheduledTaskRequest Request()
    {
        _ = Trigger.TryBuild(out ScheduleTriggerDto? trigger);
        return new(Name.Trim(), trigger!, [.. Actions.Select(action => action.Request())], int.Parse(RemainingRuns, CultureInfo.InvariantCulture),
            OverlapIndex switch { 1 => "queue", 2 => "concurrent", _ => "skip" }, Enabled, _revision);
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        UpsertScheduledTaskRequest request = Request();
        if (request.Actions.Any(action => action.Kind == "force-stop") && !await _confirm("action.save", Text("desktop.schedule-force-impact")).ConfigureAwait(true)) { return; }
        await RunAsync(async token =>
        {
            ScheduledTaskDetailsDto saved = _editingId is Guid id
                ? await _client.UpdateScheduleAsync(_instance.Id, id, request, token).ConfigureAwait(true)
                : await _client.CreateScheduleAsync(_instance.Id, request, token).ConfigureAwait(true);
            // 保存确认后保留正在编辑的控件与动作对象，避免回收模板将临时空选择写回新对象。
            _editingId = saved.Id; _revision = saved.Revision; Name = saved.Name; _baseline = Snapshot();
            OnPropertyChanged(nameof(EditorTitle)); Changed();
            await LoadCoreAsync(token).ConfigureAwait(true); Message = Text("desktop.schedule-saved-next");
        }).ConfigureAwait(true);
    }
    public async Task RunNowAsync(ScheduleRow row)
    {
        if (!CanOperate || row.Value.Actions.Any(action => action.Kind == "force-stop") && !await _confirm("action.run-now", Text("desktop.schedule-force-impact")).ConfigureAwait(true)) { return; }
        await RunAsync(async token =>
        {
            ScheduledTaskRunDto result = await _client.RunScheduleAsync(_instance.Id, row.Value.Id, token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true);
            Severity = result.Succeeded ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
            Message = Text(result.Succeeded ? "desktop.schedule-run-completed" : "desktop.schedule-run-failed-next");
            if (_editingId == row.Value.Id) { await LoadHistoryCoreAsync(token).ConfigureAwait(true); }
        }).ConfigureAwait(true);
    }
    public async Task SetEnabledAsync(ScheduleRow row, bool enabled)
    {
        if (!CanOperate || row.Enabled == enabled) { row.ResetToggle(); return; }
        await RunAsync(async token =>
        {
            ScheduledTaskDetailsDto saved = enabled ? await _client.EnableScheduleAsync(_instance.Id, row.Value.Id, token).ConfigureAwait(true) :
                await _client.DisableScheduleAsync(_instance.Id, row.Value.Id, token).ConfigureAwait(true);
            row.Update(saved); if (_editingId == saved.Id) { Populate(saved); }
            Message = Text("desktop.schedule-saved-next");
        }).ConfigureAwait(true);
        row.ResetToggle();
    }
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        ScheduleRow row = SelectedTask!;
        if (!await _confirm("action.delete", Format("desktop.schedule-delete-impact", row.Name)).ConfigureAwait(true)) { return; }
        await RunAsync(async token =>
        {
            await _client.DeleteScheduleAsync(_instance.Id, row.Value.Id, token).ConfigureAwait(true);
            IsEditorOpen = false; _editingId = null; await LoadCoreAsync(token).ConfigureAwait(true); Message = Text("common.operation-completed");
        }).ConfigureAwait(true);
    }
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task RefreshAsync()
    {
        if (!await CanLeaveAsync().ConfigureAwait(true)) { return; }
        IsEditorOpen = false; _editingId = null; await LoadAsync().ConfigureAwait(true);
    }
    [RelayCommand(CanExecute = nameof(CanCloseEditor))]
    private async Task CloseEditorAsync() { if (await CanLeaveAsync().ConfigureAwait(true)) { IsEditorOpen = false; SelectedTask = null; _editingId = null; } }
    public async Task<bool> CanLeaveAsync() { return !IsDirty || await _confirm("desktop.unsaved-title", Text("desktop.unsaved-discard-impact")).ConfigureAwait(true); }
    [RelayCommand(CanExecute = nameof(CanAddAction))]
    private void AddAction() { ScheduleActionEditor action = new(Localization); Actions.Add(action); SelectedAction = action; }
    [RelayCommand(CanExecute = nameof(CanRemoveAction))]
    private void RemoveAction() { _ = Actions.Remove(SelectedAction!); SelectedAction = Actions.FirstOrDefault(); }
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() { int index = Actions.IndexOf(SelectedAction!); Actions.Move(index, index - 1); RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() { int index = Actions.IndexOf(SelectedAction!); Actions.Move(index, index + 1); RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanViewHistory))]
    private Task ViewHistoryAsync() { return RunAsync(LoadHistoryCoreAsync); }
    private async Task LoadHistoryCoreAsync(CancellationToken token)
    {
        IReadOnlyList<ScheduledTaskRunDto> runs = await ReadRunsAsync(_editingId!.Value, token).ConfigureAwait(true);
        History = [.. runs.OrderByDescending(run => run.StartedAt).Select(run => new ScheduleRunRow(run, Localization))]; OnPropertyChanged(nameof(NoHistory));
    }
    private void OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (ScheduleActionEditor removed in _subscribedActions.Where(action => !Actions.Contains(action)).ToArray()) { removed.PropertyChanged -= OnEditorChanged; _ = _subscribedActions.Remove(removed); }
        foreach (ScheduleActionEditor added in Actions.Where(action => !_subscribedActions.Contains(action))) { added.PropertyChanged += OnEditorChanged; _ = _subscribedActions.Add(added); }
        Changed();
    }
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs args) { Changed(); }
    private void Changed()
    {
        if (_loading) { return; }
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(Validation)); OnPropertyChanged(nameof(HasValidation)); OnPropertyChanged(nameof(DirtyText)); RefreshCommands();
    }
    public void ApplyRuntimeEvent(RealtimeEventDto value)
    {
        if (value.Type != EventTypes.NodeStatusChanged || value.Payload.Deserialize<NodeStatusChangedPayload>(JsonSerializerOptions.Web) is not { } node || node.NodeId != _instance.NodeId) { return; }
        _reachable = node.Status == "Online"; OnPropertyChanged(nameof(IsOffline)); RefreshCommands();
    }
    public async Task RefreshRunEventAsync(RealtimeEventDto value)
    {
        if (value.Type != EventTypes.ScheduleRunCompleted || value.Payload.Deserialize<ScheduleRunCompletedPayload>(JsonSerializerOptions.Web) is not { } completed || completed.InstanceId != _instance.Id) { return; }
        ScheduleRow? row = Schedules.FirstOrDefault(item => item.Value.Id == completed.TaskId);
        if (row is null) { return; }
        CancellationToken token = LifetimeToken;
        try
        {
            IReadOnlyList<ScheduledTaskRunDto> runs = await ReadRunsAsync(completed.TaskId, token).ConfigureAwait(true);
            ScheduledTaskDetailsDto details = await _client.GetScheduleAsync(_instance.Id, completed.TaskId, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            ScheduledTaskRunDto? latest = runs.MaxBy(run => run.StartedAt);
            if (latest is not null && (row.LastRun is null || latest.StartedAt >= row.LastRun.StartedAt)) { row.LastRun = latest; }
            if (details.Revision >= row.Value.Revision) { row.Update(details); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ServitiumApiException or System.Net.Http.HttpRequestException)
        {
            Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
            Message = DesktopErrorPresentation.Describe(exception, Localization);
        }
    }
    protected override void RefreshCommands()
    {
        foreach (ScheduleRow row in Schedules) { row.CanOperate = CanOperate; }
        foreach (string name in new[] { nameof(CanRead), nameof(CanOperate), nameof(CanSave), nameof(CanCloseEditor), nameof(CanAddAction), nameof(CanRemoveAction), nameof(CanMoveUp), nameof(CanMoveDown), nameof(CanDelete), nameof(CanViewHistory) }) { OnPropertyChanged(name); }
        NewCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged();
        CloseEditorCommand.NotifyCanExecuteChanged(); AddActionCommand.NotifyCanExecuteChanged(); RemoveActionCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged(); MoveDownCommand.NotifyCanExecuteChanged(); ViewHistoryCommand.NotifyCanExecuteChanged();
    }
    public override void Dispose()
    {
        Actions.CollectionChanged -= OnActionsChanged; Trigger.PropertyChanged -= OnEditorChanged;
        foreach (ScheduleActionEditor action in _subscribedActions) { action.PropertyChanged -= OnEditorChanged; }
        base.Dispose();
    }
}

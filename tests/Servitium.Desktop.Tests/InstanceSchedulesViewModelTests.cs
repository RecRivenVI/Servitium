using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Scheduling;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed partial class InstanceSchedulesViewModelTests
{
    private static readonly int[] Weekdays = [1, 2, 3, 4, 5];
    [Fact]
    public async Task UndeclaredServerSuccessDoesNotCrashThePageOrDiscardEdits()
    {
        using ScheduleHandler handler = new() { InvalidSuccessStatus = true };
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceSchedulesViewModel model = Create(client);
        await model.NewCommand.ExecuteAsync(null).ConfigureAwait(true);
        model.Name = "Synthetic protocol mismatch";
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.True(model.IsDirty);
        Assert.Contains("版本", model.Message, StringComparison.Ordinal);
        Assert.True(model.CanSave);
    }
    [Fact]
    public async Task EditorRejectsValuesTheAgentRejectsAndForceStopRequiresConfirmation()
    {
        using ScheduleHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceSchedulesViewModel model = Create(client);
        await model.NewCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.False(model.IsDirty);
        model.Trigger.SpecifiedDate = DateTimeOffset.UnixEpoch.AddHours(12);
        Assert.False(model.IsDirty);
        Assert.False(model.CanSave);
        model.Name = "Synthetic task";
        Assert.True(model.CanSave);
        model.Trigger.IntervalSeconds = "9"; Assert.False(model.CanSave);
        model.Trigger.IntervalSeconds = "10"; Assert.True(model.CanSave);
        model.RemainingRuns = "0"; Assert.False(model.CanSave);
        model.RemainingRuns = "-1";
        model.Actions[0].Argument = "list\nsay unsafe"; Assert.False(model.CanSave);
        model.Actions[0].Kind = "delay"; model.Actions[0].Argument = "86401"; Assert.False(model.CanSave);
        model.Actions[0].Argument = "1"; Assert.True(model.CanSave);
        model.Actions[0].Kind = "force-stop";
        Assert.True(model.CanSave);
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Null(handler.Write);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public async Task DirtyNavigationIsCancelledAndSavingPreservesOrderedActionsAndFailurePolicy()
    {
        using ScheduleHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceSchedulesViewModel model = Create(client);
        await model.NewCommand.ExecuteAsync(null).ConfigureAwait(true);
        model.Name = "Synthetic task"; model.Enabled = false; model.OverlapIndex = 1;
        model.Actions[0].ContinueOnFailure = true;
        model.AddActionCommand.Execute(null);
        model.Actions[1].Kind = "delay"; model.Actions[1].Argument = "1";
        model.MoveUpCommand.Execute(null);
        Assert.False(await model.CanLeaveAsync().ConfigureAwait(true));
        await model.CloseEditorCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.True(model.IsEditorOpen);
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.NotNull(handler.Write);
        Assert.Null(handler.Write.Revision);
        Assert.False(handler.Write.Enabled);
        Assert.Equal("queue", handler.Write.OverlapPolicy);
        Assert.Equal("delay", handler.Write.Actions[0].Kind);
        Assert.Equal("send-console-command", handler.Write.Actions[1].Kind);
        Assert.True(handler.Write.Actions[1].ContinueOnFailure);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task LastResultUsesTheLatestRunAcrossPagesAndRealtimeDoesNotDiscardTheEditor()
    {
        using ScheduleHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        InstanceDto instance = Instance();
        using InstanceSchedulesViewModel model = Create(client, instance);
        await model.LoadAsync().ConfigureAwait(true);
        ScheduleRow row = Assert.Single(model.Schedules);
        Assert.Equal("成功", row.LastResult);
        await model.OpenEditorAsync(row).ConfigureAwait(true);
        model.Name = "Unsaved edit";
        await model.RefreshRunEventAsync(new(1, EventTypes.ScheduleRunCompleted, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new ScheduleRunCompletedPayload(instance.Id, row.Value.Id, Guid.NewGuid(), true, null)))).ConfigureAwait(true);
        Assert.Equal("Unsaved edit", model.Name);
        Assert.True(model.IsDirty);
        Assert.True(model.CanRead);
        Assert.False(row.CanOperate);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public void CalendarAndSpecifiedTriggersKeepTheirDifferentTimeSemantics()
    {
        LocalizationService localization = new(); localization.Reload("zh-Hans");
        ScheduleTriggerEditor editor = new(localization, TimeProvider.System) { KindIndex = 1, CalendarTime = TimeSpan.FromHours(9) };
        Assert.True(editor.TryBuild(out ScheduleTriggerDto? calendar));
        Assert.Equal(new TimeOnly(9, 0), calendar!.LocalTime);
        Assert.Equal(Weekdays, calendar.DaysOfWeek);
        editor.Monday = editor.Tuesday = editor.Wednesday = editor.Thursday = editor.Friday = false;
        Assert.False(editor.TryBuild(out _));
        editor.KindIndex = 2; editor.SpecifiedDate = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero); editor.SpecifiedTime = TimeSpan.FromHours(8);
        Assert.True(editor.TryBuild(out ScheduleTriggerDto? specified));
        Assert.Equal(TimeSpan.Zero, specified!.SpecifiedAt!.Value.Offset);
        Assert.Equal(new TimeOnly(8, 0), TimeOnly.FromDateTime(specified.SpecifiedAt.Value.ToLocalTime().DateTime));
    }

    [Fact]
    public async Task CancelledDeleteAndOfflineCommandsSendNoMutation()
    {
        using ScheduleHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        InstanceDto instance = Instance();
        using InstanceSchedulesViewModel model = Create(client, instance);
        await model.LoadAsync().ConfigureAwait(true);
        ScheduleRow row = Assert.Single(model.Schedules);
        await model.OpenEditorAsync(row).ConfigureAwait(true);
        await model.DeleteCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(0, handler.Deletes);
        model.ApplyRuntimeEvent(new(1, EventTypes.NodeStatusChanged, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new NodeStatusChangedPayload(instance.NodeId, "Offline", null))));
        Assert.True(model.IsOffline); Assert.False(model.CanRead); Assert.False(row.CanOperate);
        await model.RunNowAsync(row).ConfigureAwait(true);
        Assert.Equal(0, handler.Runs);
    }

    private static InstanceDto Instance() { return new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", "Stopped", @"D:\Isolated\Instance", false, []); }
    private static InstanceSchedulesViewModel Create(ServitiumApiClient client, InstanceDto? instance = null)
    {
        LocalizationService localization = new(); localization.Reload("zh-Hans");
        return new(localization, client, instance ?? Instance(), (_, _) => Task.FromResult(false));
    }
    private sealed partial class ScheduleHandler : HttpMessageHandler
    {
        internal bool InvalidSuccessStatus { get; init; }
        private ScheduledTaskDetailsDto _task = new(Guid.NewGuid(), Guid.NewGuid(), "Synthetic task", new("interval", 300, null, null, null), [new("delay", "1", false)], -1, "skip", false, 4, null);
        internal UpsertScheduledTaskRequest? Write { get; private set; }
        internal int Deletes { get; private set; }
        internal int Runs { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete) { Deletes++; throw new InvalidOperationException("Cancelled delete must not be sent."); }
            object result;
            if (request.Method == HttpMethod.Post && path.EndsWith("/run", StringComparison.Ordinal))
            {
                Runs++; result = new ScheduledTaskRunDto(Guid.NewGuid(), _task.Id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, "manual-succeeded", ["delay:succeeded"]);
            }
            else if (request.Method is { } method && (method == HttpMethod.Post || method == HttpMethod.Put))
            {
                Write = await request.Content!.ReadFromJsonAsync<UpsertScheduledTaskRequest>(cancellationToken).ConfigureAwait(false);
                _task = _task with { Name = Write!.Name, Trigger = Write.Trigger, Actions = Write.Actions, RemainingRuns = Write.RemainingRuns, OverlapPolicy = Write.OverlapPolicy, Enabled = Write.Enabled, Revision = 5 };
                result = _task;
            }
            else if (path.EndsWith("/schedules", StringComparison.Ordinal)) { result = new CursorPage<ScheduledTaskDetailsDto>([_task], null); }
            else if (path.EndsWith("/runs", StringComparison.Ordinal))
            {
                bool latest = request.RequestUri.Query.Contains("cursor=next", StringComparison.Ordinal);
                DateTimeOffset at = DateTimeOffset.UnixEpoch.AddHours(latest ? 2 : 1);
                result = new CursorPage<ScheduledTaskRunDto>([new(Guid.NewGuid(), _task.Id, at, at, latest, latest ? "manual-succeeded" : "instance-not-running", [latest ? "delay:succeeded" : "delay:instance-not-running"])], latest ? null : "next");
            }
            else { result = _task; }
            HttpStatusCode status = !InvalidSuccessStatus && request.Method == HttpMethod.Post && path.EndsWith("/schedules", StringComparison.Ordinal) ? HttpStatusCode.Created : HttpStatusCode.OK;
            return new HttpResponseMessage(status) { Content = JsonContent.Create(result) };
        }
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

using Servitium.Client;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Contracts.Events;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed partial class InstanceConsoleViewModelTests
{
    [Fact]
    public async Task SoftwareIsReadAgainAfterAuthoritativeStatusReconciliationAndUnknownClearsOldVersion()
    {
        InstanceDto instance = new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", "Stopped", @"D:\Isolated\Instance", false, []);
        InstanceSoftwareDto? software = new("1.21.1", "Fabric", "0.16.10", "fabric-launch-manifest", null, DateTimeOffset.UnixEpoch);
        bool reconciled = false;
        using RefreshingSoftwareHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                reconciled = true;
                return new InstanceRuntimeDto(instance.Id, instance.NodeId, "Stopped", null, null, null, []);
            }
            return new InstanceDetailsDto(instance with { Software = reconciled ? software : null }, new(Guid.NewGuid(), 64, 128, [], "jar", "server.jar", []), null, null, 1);
        });
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConsoleViewModel model = new(Localization(), client, instance, null, true, false, []);
        await model.LoadAsync().ConfigureAwait(true);
        Assert.Equal("1.21.1 · Fabric · 0.16.10", model.Version);
        model.Command = "say unsent";
        software = new(null, null, null, "unknown", "metadata-unreadable", DateTimeOffset.UtcNow);
        await model.LoadAsync().ConfigureAwait(true);
        Assert.Equal("尚未报告版本", model.Version);
        Assert.Contains("无法读取", model.SoftwareNote, StringComparison.Ordinal);
        Assert.Equal("say unsent", model.Command);
    }

    private sealed partial class RefreshingSoftwareHandler(Func<HttpRequestMessage, object> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(respond(request)) });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SoftwareUsesAgentMetadataAndOfflineSnapshotRemainsExplicit(bool reachable)
    {
        InstanceDto instance = new(Guid.NewGuid(), Guid.NewGuid(), "misleading-forge-9.9", "Stopped", @"D:\Isolated\Instance", false, [],
            Software: new("1.21.1", "Fabric", "0.16.10", "fabric-launch-manifest", null, DateTimeOffset.UnixEpoch));
        using ConsoleHandler handler = new(instance, "Stopped");
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConsoleViewModel model = new(Localization(), client, instance, null, reachable, false, []);
        await model.LoadAsync().ConfigureAwait(true);
        Assert.Equal("1.21.1 · Fabric · 0.16.10", model.Version);
        Assert.DoesNotContain("9.9", model.Version, StringComparison.Ordinal);
        Assert.Contains(reachable ? "启动文件" : "节点离线", model.SoftwareNote, StringComparison.Ordinal);
        Assert.Equal(instance.Software, model.Software);
    }

    [Theory]
    [InlineData("Running", true, true)]
    [InlineData("Stopped", true, false)]
    [InlineData("Crashed", true, false)]
    [InlineData("Running", false, false)]
    public async Task CommandAvailabilityAndOfflineStateFollowRuntime(string status, bool reachable, bool canSend)
    {
        InstanceDto instance = new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", status, @"D:\Isolated\Instance", false, []);
        using ConsoleHandler handler = new(instance, status);
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConsoleViewModel model = new(Localization(), client, instance, null, reachable, false, []);
        await model.LoadAsync().ConfigureAwait(true);
        model.Command = "list";
        Assert.Equal(canSend, model.SendCommand.CanExecute(null));
        Assert.Equal(!canSend, model.HasRunningNote);
        model.Command = "list\nstop";
        Assert.False(model.SendCommand.CanExecute(null));
        Assert.Equal(0, handler.PlayerQueries);
        if (!reachable) { Assert.Contains("恢复", model.StateMessage, StringComparison.Ordinal); }
        if (status == "Running" && reachable) { Assert.Contains("尚未启用", model.Players, StringComparison.Ordinal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashReportsActualExitCodeAndMemoryEvidenceWithoutInventingATimestamp(bool administrator)
    {
        InstanceDto instance = new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", "Crashed", @"D:\Isolated\Instance", false, []);
        using ConsoleHandler handler = new(instance, "Crashed", ["java.lang.OutOfMemoryError: Java heap space"]);
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConsoleViewModel model = new(Localization(), client, instance, null, true, administrator, []);
        await model.LoadAsync().ConfigureAwait(true);
        Assert.Contains("退出码 23", model.StateMessage, StringComparison.Ordinal);
        Assert.Contains("最大内存", model.StateMessage, StringComparison.Ordinal);
        Assert.Contains(administrator ? "实例设置" : "管理员", model.StateMessage, StringComparison.Ordinal);
        Assert.Contains("未报告退出时间", model.StateMessage, StringComparison.Ordinal);
        Assert.Contains("OutOfMemoryError", model.RecentOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveMetricsDoNotReloadThePageOrEraseAnUnsentCommand()
    {
        InstanceDto instance = new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", "Running", @"D:\Isolated\Instance", false, []);
        using ConsoleHandler handler = new(instance, "Running");
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConsoleViewModel model = new(Localization(), client, instance, null, true, false, []);
        await model.LoadAsync().ConfigureAwait(true);
        int reads = handler.Reads;
        model.Command = "say unfinished";
        model.ApplyMetrics(12.5, 64 * 1024 * 1024);
        model.ApplyPlayers(2, 20);
        Assert.Equal(reads, handler.Reads);
        Assert.Equal("say unfinished", model.Command);
        Assert.False(model.IsBusy);
        Assert.True(model.CanSend);
        Assert.Contains("64", model.Resources, StringComparison.Ordinal);
        Assert.Contains("2/20", model.Players, StringComparison.Ordinal);
    }

    private static LocalizationService Localization() { LocalizationService value = new(); value.Reload("zh-Hans"); return value; }
    [Fact]
    public async Task PascalCaseEventPayloadStopsCommandInputImmediatelyAndIgnoresOtherInstances()
    {
        InstanceDto instance = new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", "Running", @"D:\Isolated\Instance", false, []);
        using ConsoleHandler handler = new(instance, "Running");
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConsoleViewModel model = new(Localization(), client, instance, null, true, false, []);
        await model.LoadAsync().ConfigureAwait(true);
        model.Command = "list";
        Assert.False(model.ApplyEvent(new(1, EventTypes.InstanceStatusChanged, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new InstanceStatusChangedPayload(Guid.NewGuid(), "Running", "Stopped", null, 0)))));
        Assert.True(model.CanSend);
        Assert.True(model.ApplyEvent(new(2, EventTypes.InstanceStatusChanged, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new InstanceStatusChangedPayload(instance.Id, "Running", "Stopped", null, 0)))));
        Assert.False(model.IsRunning);
        Assert.False(model.CanSend);
        Assert.True(model.HasRunningNote);
        _ = model.ApplyEvent(new(3, EventTypes.NodeStatusChanged, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new NodeStatusChangedPayload(instance.NodeId, "Offline", null))));
        Assert.False(model.IsReachable);
        Assert.False(model.CanSend);
        Assert.Contains("恢复", model.StateMessage, StringComparison.Ordinal);
    }
    private sealed partial class ConsoleHandler(InstanceDto instance, string status, string[]? output = null) : HttpMessageHandler
    {
        internal int Reads { get; private set; }
        internal int PlayerQueries { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++;
            string path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/players", StringComparison.Ordinal)) { PlayerQueries++; throw new InvalidOperationException("Disabled query must not be requested."); }
            if (path.EndsWith("/status", StringComparison.Ordinal))
            {
                result = new InstanceRuntimeDto(instance.Id, instance.NodeId, status, null, status == "Running" ? DateTimeOffset.UtcNow : null, status == "Crashed" ? 23 : null, output ?? []);
            }
            else if (path.Contains("/java-runtimes/", StringComparison.Ordinal))
            {
                result = new JavaRuntimeDetailsDto(new(Guid.Parse(path[(path.LastIndexOf('/') + 1)..]), instance.NodeId, @"D:\Synthetic\java.exe", "25", "Synthetic", "x64", "registered", "Synthetic Java", 25, false, DateTimeOffset.UnixEpoch, null, 0), [instance.Id]);
            }
            else if (path.EndsWith("/metrics", StringComparison.Ordinal)) { result = new InstanceMetricsDto(DateTimeOffset.UtcNow, 1.5, 1024, null); }
            else
            {
                StructuredLaunchRequest launch = new(Guid.NewGuid(), 64, 128, [], "jar", "server.jar", []);
                UpdateInstanceRequest configuration = new(instance.Name, [], null, launch, "stop", 60, false, false, 3, new(false, 25575, null), new(false, "127.0.0.1", 25565));
                result = new InstanceDetailsDto(instance, launch, null, configuration, 1);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Nodes;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed partial class InstanceWorkspaceViewModelTests
{
    [Fact]
    public async Task SoftwareObservationUpdatesListAndTitleWithoutLosingSelectionAndUnknownNeverGuesses()
    {
        InstanceDto instance = Instance("Stopped");
        using DetailsHandler handler = new(instance);
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceWorkspaceViewModel model = new(Localization(), client, false, (_, _) => Task.FromResult(false), _ => { });
        model.ApplySnapshot([Group(instance, true)]);
        await model.SelectAsync(model.Groups[0].Items[0]).ConfigureAwait(true);
        InstanceRow selected = model.Selected!;
        model.ApplySoftware(instance.Id, new("1.21.1", "NeoForge", "21.1.80", "fml-argument-file", null, DateTimeOffset.UtcNow));
        Assert.Same(selected, model.Selected);
        Assert.Equal("1.21.1 · NeoForge · 21.1.80", selected.Version);
        Assert.Contains(selected.Version, model.Subtitle, StringComparison.Ordinal);
        model.Filter = "合成";
        Assert.Equal(selected.Version, model.Groups[0].Items[0].Version);
        model.ApplySoftware(instance.Id, new(null, null, null, "unknown", "metadata-conflict", DateTimeOffset.UtcNow));
        Assert.Equal("尚未报告版本", model.Groups[0].Items[0].Version);
        Assert.DoesNotContain("NeoForge", model.Subtitle, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Running", true, false, true, true)]
    [InlineData("Stopped", true, true, false, false)]
    [InlineData("Crashed", true, true, false, false)]
    [InlineData("Running", false, false, false, false)]
    public async Task LifecycleAvailabilityUsesBothInstanceStateAndNodeReachability(string status, bool online, bool start, bool stop, bool force)
    {
        InstanceDto instance = Instance(status);
        using DetailsHandler handler = new(instance);
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceWorkspaceViewModel model = new(Localization(), client, true, (_, _) => Task.FromResult(true), _ => { });
        model.ApplySnapshot([Group(instance, online)]);
        await model.SelectAsync(model.Groups[0].Items[0]).ConfigureAwait(true);
        Assert.Equal(start, model.StartCommand.CanExecute(null));
        Assert.Equal(stop, model.StopCommand.CanExecute(null));
        Assert.Equal(stop, model.RestartCommand.CanExecute(null));
        Assert.Equal(force, model.ForceStopCommand.CanExecute(null));
        Assert.Equal(!online || status == "Crashed", model.HasStateMessage);
        if (!online) { Assert.Contains("恢复", model.StateMessage, StringComparison.Ordinal); }
    }

    [Fact]
    public async Task OrdinaryUserCannotForceStopAndCancelledConfirmationNeverQueuesACommand()
    {
        InstanceDto instance = Instance("Running");
        using DetailsHandler handler = new(instance);
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceWorkspaceViewModel ordinary = new(Localization(), client, false, (_, _) => Task.FromResult(true), _ => { });
        ordinary.ApplySnapshot([Group(instance, true)]);
        await ordinary.SelectAsync(ordinary.Groups[0].Items[0]).ConfigureAwait(true);
        Assert.False(ordinary.ForceStopCommand.CanExecute(null));
        Assert.False(ordinary.IsAdministrator);
        using InstanceWorkspaceViewModel owner = new(Localization(), client, true, (_, _) => Task.FromResult(false), _ => { });
        owner.ApplySnapshot([Group(instance, true)]);
        await owner.SelectAsync(owner.Groups[0].Items[0]).ConfigureAwait(true);
        await owner.ForceStopCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.False(owner.HasMessage);
        Assert.True(owner.CanStop);
    }

    [Fact]
    public void StatusAndTextFiltersKeepOfflineInstancesVisibleAndHideEmptyNodesFromOrdinaryUsers()
    {
        InstanceDto instance = Instance("Running");
        using DetailsHandler handler = new(instance);
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceWorkspaceViewModel model = new(Localization(), client, false, (_, _) => Task.FromResult(false), _ => { });
        model.ApplySnapshot([Group(instance, false), Group(instance, true) with { Items = [] }]);
        _ = Assert.Single(model.Groups);
        Assert.Equal("节点离线", model.Groups[0].Items[0].Status);
        Assert.DoesNotContain("InstanceNodeGroup", model.Groups[0].ToString(), StringComparison.Ordinal);
        model.StatusFilter = 3;
        _ = Assert.Single(model.Groups[0].Items);
        model.Filter = "不存在的名字";
        Assert.True(model.IsEmpty);
        Assert.Empty(model.Groups);
    }

    private static InstanceDto Instance(string status) { return new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", status, @"D:\Isolated\Instance", false, [], "合成节点"); }
    private static InstanceGroupDto Group(InstanceDto instance, bool online) { return new(new NodeDto(instance.NodeId, "合成节点", online ? "Online" : "Offline", null, DateTimeOffset.UtcNow), [instance], null); }
    private static LocalizationService Localization() { LocalizationService value = new(); value.Reload("zh-Hans"); return value; }
    private sealed partial class DetailsHandler(InstanceDto instance) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            InstanceDetailsDto details = new(instance, new(Guid.NewGuid(), 64, 128, [], "jar", "server.jar", []), null, null, 1);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(details) });
        }
    }
}

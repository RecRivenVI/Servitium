using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed partial class InstanceModsViewModelTests
{
    [Theory]
    [InlineData("Running", true, true, false)]
    [InlineData("Stopped", true, true, true)]
    [InlineData("Crashed", true, true, true)]
    [InlineData("Stopped", false, false, false)]
    public async Task EnableChangesAndFileMutationsRespectTheirDifferentAgentGates(string status, bool reachable, bool toggles, bool mutations)
    {
        using ModHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceModsViewModel model = Create(client, status, reachable);
        await model.LoadAsync().ConfigureAwait(true);
        model.SelectedMod = model.Mods[0];
        Assert.Equal(2, model.Mods.Count);
        Assert.Equal(toggles, model.Mods[0].CanToggle);
        Assert.Equal(mutations, model.UploadCommand.CanExecute(null));
        Assert.Equal(mutations, model.DeleteCommand.CanExecute(null));
        if (mutations) { await model.DeleteCommand.ExecuteAsync(null).ConfigureAwait(true); }
        Assert.Equal(0, handler.Deletes);
    }

    [Fact]
    public async Task ToggleFailureRestoresTheActualStateAndDoesNotClaimSuccess()
    {
        using ModHandler handler = new() { RejectToggle = true };
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceModsViewModel model = Create(client);
        await model.LoadAsync().ConfigureAwait(true);
        ModRow row = model.Mods[0];
        await model.SetEnabledAsync(row, false).ConfigureAwait(true);
        Assert.True(row.Enabled);
        Assert.True(row.CanToggle);
        Assert.Contains("占用", model.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mod-internal-id", row.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiltersKeepMetadataSearchAndThePluginSelectorUsesTheExistingKind()
    {
        using ModHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceModsViewModel model = Create(client);
        await model.LoadAsync().ConfigureAwait(true);
        model.Search = "Fixture Author";
        Assert.Equal(2, model.Mods.Count);
        model.EnabledFilter = 2;
        _ = Assert.Single(model.Mods);
        model.Search = "missing";
        Assert.True(model.IsEmpty);
        model.Search = string.Empty; model.EnabledFilter = 0;
        await model.ChangeKindAsync(1).ConfigureAwait(true);
        Assert.True(handler.ReadPlugins);
        model.SelectedMod = model.Mods[0];
        await model.SetEnabledAsync(model.SelectedMod, false).ConfigureAwait(true);
        Assert.False(model.SelectedMod.Enabled);
        await model.UploadCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.False(model.IsUploading);
        Assert.True(model.CanUpload);
    }

    private static InstanceModsViewModel Create(ServitiumApiClient client, string status = "Stopped", bool reachable = true)
    {
        LocalizationService localization = new(); localization.Reload("zh-Hans");
        InstanceDto instance = new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", status, @"D:\Isolated\Instance", false, []);
        return new(localization, client, instance, () => Task.FromResult<string?>(null), (_, _) => Task.FromResult(false), _ => { }, reachable);
    }
    private sealed partial class ModHandler : HttpMessageHandler
    {
        internal bool RejectToggle { get; init; }
        internal bool ReadPlugins { get; private set; }
        internal int Deletes { get; private set; }
        private static ModDetailsDto Mod(bool enabled) { return new("mod-internal-id", "mods/synthetic.jar", "Synthetic mod", "1.0", "Synthetic metadata", ["Fixture Author"], "fabric", enabled, 100, DateTimeOffset.UnixEpoch, [], false, "synthetic"); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) { Deletes++; throw new InvalidOperationException("Cancelled delete must not be sent."); }
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(RejectToggle
                    ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { code = "file-locked" }) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Mod(false)) });
            }
            ReadPlugins |= request.RequestUri!.Query.Contains("kind=plugins", StringComparison.Ordinal);
            bool more = request.RequestUri.Query.Contains("cursor=next", StringComparison.Ordinal);
            object result = new CursorPage<ModDetailsDto>([Mod(!more)], more ? null : "next");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}

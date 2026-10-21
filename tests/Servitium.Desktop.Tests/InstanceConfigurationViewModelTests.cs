using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed partial class InstanceConfigurationViewModelTests
{
    [Fact]
    public async Task InvalidFieldsCannotSaveAndValidWritesContainOnlyChangesWithTheReadRevision()
    {
        using ConfigurationHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConfigurationViewModel model = Create(client);
        await model.LoadAsync().ConfigureAwait(true);
        Assert.True(model.ShowStructured);
        Assert.False(model.IsDirty);
        model.ServerPort.Value = "65536";
        Assert.True(model.HasValidation);
        Assert.False(model.SaveCommand.CanExecute(null));
        model.ServerPort.Value = "25566";
        model.RconPassword.Value = "synthetic-secret-not-for-display";
        Assert.Equal(2, model.DirtyCount);
        Assert.DoesNotContain("synthetic-secret", model.DirtyDetails, StringComparison.Ordinal);
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.NotNull(handler.Write);
        Assert.Equal("private-revision", handler.Write.ExpectedRevision);
        Assert.Equal(2, handler.Write.Values!.Count);
        Assert.Equal("25566", handler.Write.Values["server-port"]);
        Assert.DoesNotContain("unknown-fixture-field", handler.Write.Values.Keys);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task CancelledNavigationKeepsDirtyValuesAndViewMode()
    {
        using ConfigurationHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConfigurationViewModel model = Create(client);
        await model.LoadAsync().ConfigureAwait(true);
        model.ServerPort.Value = "25567";
        Assert.False(await model.CanLeaveAsync().ConfigureAwait(true));
        await model.SwitchViewAsync(true).ConfigureAwait(true);
        await model.OpenAsync(model.Files[1]).ConfigureAwait(true);
        Assert.False(model.IsRaw);
        Assert.Equal("server.properties", model.SelectedFile!.Path);
        Assert.Equal("25567", model.ServerPort.Value);
        model.DiscardCommand.Execute(null);
        Assert.False(model.IsDirty);
        await model.OpenAsync(model.Files[1]).ConfigureAwait(true);
        Assert.True(model.IsRaw);
        Assert.Equal("yaml", model.EditorLanguage);
    }

    [Fact]
    public async Task ConcurrentSaveFailureKeepsEditsAndGivesARefreshNextStep()
    {
        using ConfigurationHandler handler = new() { RejectWrite = true };
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConfigurationViewModel model = Create(client);
        await model.LoadAsync().ConfigureAwait(true);
        model.ServerPort.Value = "25568";
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.True(model.IsDirty);
        Assert.True(model.SaveCommand.CanExecute(null));
        Assert.Contains("重新", model.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-revision", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeTransitionsKeepReadAccessButRevokeWritesAndCancelledRestoreSendsNoRequest()
    {
        InstanceDto instance = Instance();
        using ConfigurationHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceConfigurationViewModel model = Create(client, instance);
        await model.LoadAsync().ConfigureAwait(true);
        model.ServerPort.Value = "25569";
        model.ApplyRuntimeEvent(new(1, EventTypes.InstanceStatusChanged, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new InstanceStatusChangedPayload(instance.Id, "Stopped", "Running", null, null))));
        Assert.False(model.CanSave); Assert.True(model.CanRead);
        model.ApplyRuntimeEvent(new(2, EventTypes.InstanceStatusChanged, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new InstanceStatusChangedPayload(instance.Id, "Running", "Stopped", null, 0))));
        Assert.True(model.CanSave);
        await model.ViewBackupsCommand.ExecuteAsync(null).ConfigureAwait(true);
        model.SelectedBackup = Assert.Single(model.Backups);
        await model.RestoreBackupCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(0, handler.Restores);
        model.ApplyRuntimeEvent(new(3, EventTypes.NodeStatusChanged, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new NodeStatusChangedPayload(instance.NodeId, "Offline", null))));
        Assert.False(model.CanRead); Assert.False(model.CanSave);
    }

    private static InstanceDto Instance() { return new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", "Stopped", @"D:\Isolated\Instance", false, []); }
    private static InstanceConfigurationViewModel Create(ServitiumApiClient client, InstanceDto? instance = null)
    {
        LocalizationService localization = new(); localization.Reload("zh-Hans");
        return new(localization, client, instance ?? Instance(), (_, _) => Task.FromResult(false), _ => { });
    }
    private sealed partial class ConfigurationHandler : HttpMessageHandler
    {
        internal WriteConfigurationRequest? Write { get; private set; }
        internal bool RejectWrite { get; init; }
        internal int Restores { get; private set; }
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal) { ["server-port"] = "25565", ["unknown-fixture-field"] = "must-stay" };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                Write = await request.Content!.ReadFromJsonAsync<WriteConfigurationRequest>(cancellationToken).ConfigureAwait(false);
                if (RejectWrite) { return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { code = "concurrency-conflict" }) }; }
                foreach (KeyValuePair<string, string?> item in Write!.Values!) { _values[item.Key] = item.Value; }
            }
            object result;
            if (path.EndsWith("/restore", StringComparison.Ordinal)) { Restores++; throw new InvalidOperationException("Cancelled restore must not be sent."); }
            if (path.EndsWith("/backups", StringComparison.Ordinal)) { result = new CursorPage<ConfigurationBackupDto>([new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, 20, "private-backup-revision")], null); }
            else if (path.EndsWith("/configurations", StringComparison.Ordinal))
            {
                result = new CursorPage<ConfigurationDto>([new("server-properties", "server.properties", "properties", false, null, DateTimeOffset.UnixEpoch), new("bukkit", "bukkit.yml", "yaml", false, null, DateTimeOffset.UnixEpoch)], null);
            }
            else
            {
                bool yaml = path.EndsWith("/bukkit", StringComparison.Ordinal);
                result = new ConfigurationDetailsDto(yaml ? "bukkit" : "server-properties", yaml ? "bukkit.yml" : "server.properties", yaml ? "yaml" : "properties", _values,
                    yaml ? "settings: {}" : "server-port=25565\nunknown-fixture-field=must-stay\n", "private-revision", DateTimeOffset.UnixEpoch);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}

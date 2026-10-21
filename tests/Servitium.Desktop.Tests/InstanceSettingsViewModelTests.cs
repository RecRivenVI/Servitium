using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed partial class InstanceSettingsViewModelTests
{
    [Fact]
    public async Task SavingOneFieldKeepsLaunchTerminalLifecycleAndSavedPasswordSemantics()
    {
        using SettingsHandler handler = new();
        using ServitiumApiClient client = Client(handler);
        using InstanceSettingsViewModel model = Create(client, handler);
        await model.LoadAsync().ConfigureAwait(true);
        Assert.True(model.HasData); Assert.False(model.IsDirty); Assert.False(model.HasValidation);
        model.Notes = "Changed note";
        Assert.True(model.CanSave);
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        UpdateInstanceRequest write = Assert.IsType<UpdateInstanceRequest>(handler.Write);
        Assert.Equal("Changed note", write.Notes);
        Assert.Null(write.WorkingDirectory);
        Assert.Null(write.Rcon.Password);
        Assert.Equal("GBK", write.Terminal!.InputEncoding);
        Assert.Equal("Big5", write.Terminal.OutputEncoding);
        Assert.Equal("LF", write.Terminal.NewLine);
        Assert.False(write.Terminal.UseConPty); Assert.False(write.Terminal.AnsiEnabled);
        Assert.Equal(200, write.Terminal.Columns); Assert.Equal(50, write.Terminal.Rows);
        Assert.True(write.AutoRestart); Assert.Equal(7, write.MaximumRestartAttempts);
        Assert.Equal(12, write.AutoStartDelaySeconds); Assert.Equal(8, write.RestartIntervalSeconds); Assert.Equal(700, write.RestartResetSeconds);
        Assert.Equal(["-Dname=two words"], write.Launch.JvmArguments);
        Assert.Equal("synthetic-value", write.Launch.EnvironmentVariables!["KEEP"]);
        Assert.Equal(["one,comma", " spaced "], write.Tags);
        Assert.Equal("say existing", Assert.Single(write.ShortcutCommands!).Command);
        Assert.False(model.IsDirty);
        model.ClearRconPassword = true;
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(string.Empty, handler.Write!.Rcon.Password);
        Assert.False(model.ClearRconPassword); Assert.Equal(string.Empty, model.RconPassword);
    }

    [Fact]
    public async Task SavingNotesPreservesOptionalLaunchAndConfigurationBlocksExactly()
    {
        using SettingsHandler handler = new(); handler.UseOptionalConfiguration();
        using ServitiumApiClient client = Client(handler); using InstanceSettingsViewModel model = Create(client, handler);
        await model.LoadAsync().ConfigureAwait(true); model.Notes = "Changed only the note";
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        UpdateInstanceRequest write = Assert.IsType<UpdateInstanceRequest>(handler.Write);
        Assert.Null(write.Launch.EnvironmentVariables); Assert.Null(write.Launch.AdvancedArguments);
        Assert.Null(write.UpdateAction); Assert.Null(write.ShortcutCommands);
        Assert.Equal("utf-8", write.Terminal!.InputEncoding); Assert.Equal(string.Empty, write.RunCredentialAlias);
        model.Launch.MinimumHeap = 80; await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(80, handler.Write!.Launch.MinimumHeapMegabytes);
    }

    [Fact]
    public async Task KnownConcurrentEditsKeepTheFormAndNeverSendAnOverwrite()
    {
        using SettingsHandler handler = new();
        using ServitiumApiClient client = Client(handler);
        using InstanceSettingsViewModel model = Create(client, handler);
        await model.LoadAsync().ConfigureAwait(true);
        model.Notes = "Local edit"; handler.BumpRevision();
        await model.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Null(handler.Write);
        Assert.True(model.IsDirty);
        Assert.Equal("Local edit", model.Notes);
        Assert.Contains("重新", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidNumbersAndStructuredFieldsDisableSaveWithoutBreakingDirtyTracking()
    {
        using SettingsHandler handler = new();
        using ServitiumApiClient client = Client(handler);
        using InstanceSettingsViewModel model = Create(client, handler);
        await model.LoadAsync().ConfigureAwait(true);
        model.TerminalColumns = double.NaN;
        Assert.True(model.IsDirty); Assert.False(model.CanSave);
        model.TerminalColumns = 200; model.RconPort = 65536; Assert.False(model.CanSave);
        model.RconPort = 25575; model.UpdateKind = 1; model.UpdateSource = "new.jar"; model.UpdateTarget = @"..\outside.jar";
        Assert.False(model.CanSave);
        model.UpdateTarget = "server.jar"; Assert.True(model.CanSave);
        model.UpdateKind = 2; Assert.False(model.CanSave);
        model.SelectedChannel = Assert.Single(model.Channels); Assert.True(model.CanSave);
        model.RconPassword = "synthetic"; model.ClearRconPassword = true; Assert.False(model.CanSave);
        model.ClearRconPassword = false; model.AddShortcutCommand.Execute(null); Assert.False(model.CanSave);
        model.Shortcuts[1].Name = "New command"; model.Shortcuts[1].CommandText = "say one\nsay two"; Assert.False(model.CanSave);
        model.Shortcuts[1].CommandText = "say one"; Assert.True(model.CanSave);
    }

    [Theory]
    [InlineData("Running", false)]
    [InlineData("Stopped", true)]
    [InlineData("Crashed", true)]
    public async Task TheExistingStoppedStateGateAppliesToAllSettingsAndRuntimeEventsRevokeEditing(string status, bool editable)
    {
        using SettingsHandler handler = new(status);
        using ServitiumApiClient client = Client(handler);
        using InstanceSettingsViewModel model = Create(client, handler);
        await model.LoadAsync().ConfigureAwait(true);
        model.Notes = "Draft";
        Assert.Equal(editable, model.CanSave);
        Assert.Equal(editable, model.CanUnregister);
        _ = model.ApplyRuntimeEvent(new(1, EventTypes.NodeStatusChanged, DateTimeOffset.UtcNow, "global",
            JsonSerializer.SerializeToElement(new NodeStatusChangedPayload(handler.Instance.NodeId, "Offline", null))));
        Assert.False(model.CanRead); Assert.False(model.CanSave);
        Assert.Equal("Draft", model.Notes);
        Assert.Contains("恢复", model.Restriction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryUserLoadsNoAdministrativeDataAndCancelledDangerActionsSendNoMutation()
    {
        using SettingsHandler handler = new();
        using ServitiumApiClient client = Client(handler);
        using (InstanceSettingsViewModel ordinary = Create(client, handler, administrator: false))
        {
            await ordinary.LoadAsync().ConfigureAwait(true);
            Assert.Equal(0, handler.Reads); Assert.False(ordinary.CanRead); Assert.False(ordinary.HasData);
        }
        using InstanceSettingsViewModel owner = Create(client, handler);
        await owner.LoadAsync().ConfigureAwait(true);
        Assert.False(owner.CanDeleteDirectory);
        await owner.UnregisterCommand.ExecuteAsync(null).ConfigureAwait(true);
        owner.WorkingDirectory = @"D:\AnotherInstance";
        await owner.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(0, handler.Deletes); Assert.Null(handler.Write);
        using SettingsHandler managed = new(owned: true);
        using ServitiumApiClient managedClient = Client(managed);
        using InstanceSettingsViewModel managedModel = Create(managedClient, managed);
        await managedModel.LoadAsync().ConfigureAwait(true);
        Assert.True(managedModel.CanDeleteDirectory);
        await managedModel.DeleteDirectoryCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(0, managed.Deletes);
    }

    [Fact]
    public async Task DirtyRefreshCanBeCancelledAndMissingConfigurationIsNotFilledWithInventedDefaults()
    {
        using SettingsHandler handler = new();
        using ServitiumApiClient client = Client(handler);
        using InstanceSettingsViewModel model = Create(client, handler);
        await model.LoadAsync().ConfigureAwait(true);
        model.Notes = "Draft"; int reads = handler.Reads;
        await model.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(reads, handler.Reads); Assert.Equal("Draft", model.Notes);
        model.DiscardCommand.Execute(null); Assert.False(model.IsDirty);
        using SettingsHandler missing = new() { MissingConfiguration = true };
        using ServitiumApiClient missingClient = Client(missing);
        using InstanceSettingsViewModel missingModel = Create(missingClient, missing);
        await missingModel.LoadAsync().ConfigureAwait(true);
        Assert.False(missingModel.HasData); Assert.False(missingModel.CanEdit);
        Assert.Contains("版本", missingModel.Message, StringComparison.Ordinal);
    }

    private static ServitiumApiClient Client(SettingsHandler handler) { return new(new Uri("http://127.0.0.1:58132"), handler: handler); }
    private static InstanceSettingsViewModel Create(ServitiumApiClient client, SettingsHandler handler, bool administrator = true)
    {
        LocalizationService localization = new(); localization.Reload("zh-Hans");
        return new(localization, client, handler.Instance, administrator, (_, _) => Task.FromResult(false), (_, _, _) => Task.FromResult<string?>(null));
    }
    private sealed partial class SettingsHandler : HttpMessageHandler
    {
        internal InstanceDto Instance { get; private set; }
        internal UpdateInstanceRequest? Write { get; private set; }
        internal bool MissingConfiguration { get; init; }
        internal int Reads { get; private set; }
        internal int Deletes { get; private set; }
        private readonly JavaRuntimeDto _java;
        private UpdateInstanceRequest _settings;
        private long _revision = 1;
        internal SettingsHandler(string status = "Stopped", bool owned = false)
        {
            var nodeId = Guid.NewGuid();
            Instance = new(Guid.NewGuid(), nodeId, "合成实例", status, @"D:\Isolated\Instance", owned, ["one,comma", " spaced "]);
            _java = new(Guid.NewGuid(), nodeId, @"D:\Java\bin\java.exe", "25", "Synthetic", "x64", "registered", "Java 25", 25, false, DateTimeOffset.UnixEpoch, null, 0);
            _settings = new(Instance.Name, Instance.Tags, "Original note", new(_java.Id, 64, 128, ["-Dname=two words"], "jar", "fixture.jar", [], new Dictionary<string, string>(StringComparer.Ordinal) { ["KEEP"] = "synthetic-value" }),
                "stop", 30, false, true, 7, new(false, 25575, null), new(false, "127.0.0.1", 25565), AutoStartDelaySeconds: 12, RestartIntervalSeconds: 8, RestartResetSeconds: 700,
                Terminal: new(false, 200, 50, "GBK", "Big5", "LF", false), ShortcutCommands: [new("Existing shortcut", "say existing")]);
        }
        internal void UseOptionalConfiguration()
        {
            _settings = _settings with
            {
                Launch = _settings.Launch with { EnvironmentVariables = null, AdvancedArguments = null },
                UpdateAction = null,
                ShortcutCommands = null,
                RunCredentialAlias = string.Empty,
                Terminal = _settings.Terminal! with { InputEncoding = "utf-8" }
            };
        }
        internal void BumpRevision() { _revision++; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete) { Deletes++; throw new InvalidOperationException("Cancelled deletion must not reach the Server."); }
            if (request.Method == HttpMethod.Patch)
            {
                Write = await request.Content!.ReadFromJsonAsync<UpdateInstanceRequest>(cancellationToken).ConfigureAwait(false);
                _settings = Write! with { Rcon = Write.Rcon with { Password = null } };
                Instance = Instance with { Name = Write.Name, Tags = Write.Tags }; _revision++;
            }
            else { Reads++; }
            object result;
            if (path.EndsWith("/java-runtimes", StringComparison.Ordinal)) { result = new CursorPage<JavaRuntimeDto>([_java], null); }
            else if (path.EndsWith("/channels", StringComparison.Ordinal)) { result = new CursorPage<HmclChannelDto>([new(Guid.NewGuid(), "stable", "稳定通道", "public", null)], null); }
            else
            {
                result = path.EndsWith("/nodes/" + Instance.NodeId.ToString("D"), StringComparison.Ordinal)
                ? new NodeDetailsDto(Instance.NodeId, "合成节点", "Online", "fixture", 1, 0, "Windows", "x64", DateTimeOffset.UnixEpoch, null, new("合成节点", null, null, null))
                : new InstanceDetailsDto(Instance, _settings.Launch, _settings.Notes, MissingConfiguration ? null : _settings, _revision);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}

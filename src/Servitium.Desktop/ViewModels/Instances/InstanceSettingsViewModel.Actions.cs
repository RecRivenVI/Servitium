using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Operations;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceSettingsViewModel
{
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (DirectoryChanged && !await _confirm("action.save", Format("desktop.settings-change-directory-impact", WorkingDirectory, _details!.Instance.WorkingDirectory)).ConfigureAwait(true)) { return; }
        if (!CanSave) { return; }
        UpdateInstanceRequest request = Request();
        await RunAsync(async token =>
        {
            InstanceDetailsDto current = await _client.GetInstanceAsync(_instance.Id, token).ConfigureAwait(true);
            if (current.Revision != _details!.Revision) { throw new ServitiumApiException(HttpStatusCode.Conflict, "concurrency-conflict"); }
            InstanceDetailsDto saved = await _client.UpdateInstanceAsync(_instance.Id, request, token).ConfigureAwait(true);
            _loading = true;
            try
            {
                _details = saved; _status = saved.Instance.Status; Name = saved.Instance.Name; WorkingDirectory = saved.Instance.WorkingDirectory;
                Launch.Target = saved.Launch.Target; RconPassword = string.Empty; ClearRconPassword = false; _baseline = Snapshot();
                OnPropertyChanged(nameof(Ownership)); OnPropertyChanged(nameof(IsOwned));
            }
            finally { _loading = false; Changed(); }
            Message = Text("desktop.settings-saved-next");
        }).ConfigureAwait(true);
    }
    [RelayCommand(CanExecute = nameof(CanUnregister))]
    private async Task UnregisterAsync()
    {
        if (!await _confirm("desktop.settings-unregister-title", Format("desktop.settings-unregister-impact", _details!.Instance.Name, _details.Instance.WorkingDirectory)).ConfigureAwait(true)) { return; }
        await RunAsync(async token =>
        {
            await _client.UnregisterInstanceAsync(_instance.Id, _instance.NodeId, token).ConfigureAwait(true);
            HasData = false; RconPassword = string.Empty; Removed?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(true);
    }
    [RelayCommand(CanExecute = nameof(CanDeleteDirectory))]
    private async Task DeleteDirectoryAsync()
    {
        string? name = await _prompt("desktop.settings-delete-directory-title", "desktop.settings-confirm-name", null).ConfigureAwait(true);
        if (name is null) { return; }
        if (!string.Equals(name, _details!.Instance.Name, StringComparison.Ordinal)) { Message = Text("desktop.settings-name-confirm-mismatch"); return; }
        if (!await _confirm("desktop.settings-delete-directory-title", Format("desktop.settings-delete-directory-impact", _details.Instance.WorkingDirectory)).ConfigureAwait(true)) { return; }
        if (!CanDeleteDirectory) { return; }
        await RunAsync(async token =>
        {
            OperationAcceptedDto accepted = await _client.DeleteInstanceWorkingDirectoryAsync(_instance.Id, _instance.NodeId, token).ConfigureAwait(true);
            _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, token).ConfigureAwait(true);
            HasData = false; RconPassword = string.Empty; Removed?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(true);
    }
    public bool ApplyRuntimeEvent(RealtimeEventDto value)
    {
        bool reload = false;
        if (value.Type == EventTypes.NodeStatusChanged && value.Payload.Deserialize<NodeStatusChangedPayload>(JsonSerializerOptions.Web) is { } node && node.NodeId == _instance.NodeId)
        {
            bool wasReachable = _reachable; _reachable = node.Status == "Online";
            reload = !wasReachable && _reachable && !HasData;
        }
        if (value.Type == EventTypes.InstanceStatusChanged && value.Payload.Deserialize<InstanceStatusChangedPayload>(JsonSerializerOptions.Web) is { } status && status.InstanceId == _instance.Id) { _status = status.Status; }
        Changed(); return reload;
    }
}

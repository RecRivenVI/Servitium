using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class ConnectionsViewModel(LocalizationService localization, ServerProfileService profiles,
    Func<Uri, Task> connect) : DesktopViewModel(localization)
{
    public ObservableCollection<ServerProfile> Profiles { get; } = [.. profiles.Profiles];
    public ServerProfile? Selected
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) { return; }
            PendingDelete = null;
            ProfileName = value?.Name ?? string.Empty;
            OnPropertyChanged(nameof(SelectedAddress)); OnPropertyChanged(nameof(CanDelete)); RefreshCommands();
        }
    }
    public string SelectedAddress => Selected?.Address.AbsoluteUri ?? string.Empty;
    public string Address { get; set { if (SetProperty(ref field, value)) { RefreshCommands(); } } } = string.Empty;
    public string ProfileName { get; set { if (SetProperty(ref field, value)) { RefreshCommands(); } } } = string.Empty;
    public string CurrentConnection => Format("desktop.settings-current-connection", profiles.Active.Name, profiles.Active.Address.AbsoluteUri);
    private bool CanConnect()
    {
        return CanInteract && Selected is not null;
    }

    private bool CanAdd()
    {
        return CanInteract && TryAddress(Address, out _);
    }

    private bool CanRename()
    {
        return CanInteract && Selected is not null && !string.IsNullOrWhiteSpace(ProfileName) && ProfileName.Trim().Length <= 80;
    }

    public bool CanDelete => CanInteract && Selected is not null && Selected.Id != profiles.Active.Id && Profiles.Count > 1;
    internal static bool TryAddress(string value, out Uri? address)
    {
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out address) && address.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(address.UserInfo) && string.IsNullOrEmpty(address.Query) && string.IsNullOrEmpty(address.Fragment);
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddAsync()
    {
        return RunAsync(async _ => { if (TryAddress(Address, out Uri? address)) { await connect(address!).ConfigureAwait(true); } });
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync()
    {
        return RunAsync(async _ => { await connect(Selected!.Address).ConfigureAwait(true); });
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private Task RenameAsync()
    {
        return RunAsync(async token =>
    {
        Guid id = Selected!.Id;
        await profiles.RenameAsync(id, ProfileName, token).ConfigureAwait(true);
        Reload(id); Message = Text("desktop.local-settings-saved"); OnPropertyChanged(nameof(CurrentConnection));
    });
    }

    public ServerProfile? PendingDelete { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(IsConfirmingDelete)); ConfirmDeleteCommand.NotifyCanExecuteChanged(); } } }
    public bool IsConfirmingDelete => PendingDelete is not null;
    private bool CanConfirmDelete()
    {
        return CanInteract && PendingDelete is not null;
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete() { PendingDelete = Selected; }
    [RelayCommand]
    private void CancelDelete() { PendingDelete = null; }
    [RelayCommand(CanExecute = nameof(CanConfirmDelete))]
    private Task ConfirmDeleteAsync()
    {
        return RunAsync(async token =>
    {
        await profiles.DeleteAsync(PendingDelete!.Id, token).ConfigureAwait(true); PendingDelete = null; Reload(null);
    });
    }

    private void Reload(Guid? selected)
    {
        Profiles.Clear(); foreach (ServerProfile profile in profiles.Profiles) { Profiles.Add(profile); if (profile.Id == selected) { Selected = profile; } }
        if (selected is null) { Selected = null; }
    }
    protected override void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanDelete)); AddCommand.NotifyCanExecuteChanged(); ConnectCommand.NotifyCanExecuteChanged(); RenameCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); ConfirmDeleteCommand.NotifyCanExecuteChanged();
    }
}

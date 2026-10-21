using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Servitium.Desktop.AppModel;
using Servitium.Desktop.ViewModels.Instances;

namespace Servitium.Desktop.Views.Instances;

internal sealed partial class InstanceCreationDialog : ContentDialog
{
    private readonly AppServices _services;
    public InstanceCreationViewModel ViewModel { get; }
    internal InstanceCreationDialog(AppServices services)
    {
        _services = services; ViewModel = new(services.Localization, services.Session.Client);
        InitializeComponent();
        Opened += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);
        Closed += (_, _) => ViewModel.Dispose();
        ViewModel.Submitted += (_, _) => Hide();
    }
    private async void OnNodeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel.SelectedNode is not null) { await ViewModel.LoadJavaAsync().ConfigureAwait(true); }
    }
    private async void OnChooseArchiveClick(object sender, RoutedEventArgs args)
    {
        string? path = await _services.Interaction.PickOpenFileAsync(".zip").ConfigureAwait(true);
        if (path is not null) { ViewModel.ArchivePath = path; }
    }
    private void OnRemoveEnvironment(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: LaunchEnvironmentVariable variable }) { ViewModel.Launch.RemoveEnvironmentCommand.Execute(variable); }
    }
}

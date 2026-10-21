using System;

using Microsoft.UI.Xaml.Controls;

using Servitium.Desktop.ViewModels.Settings;

namespace Servitium.Desktop.Views.Settings;

internal sealed partial class ConnectionsView : UserControl, IDisposable
{
    public ConnectionsViewModel ViewModel { get; }
    internal ConnectionsView(ConnectionsViewModel model) { ViewModel = model; InitializeComponent(); Unloaded += (_, _) => Dispose(); }
    public void Dispose() { ViewModel.Dispose(); GC.SuppressFinalize(this); }
}

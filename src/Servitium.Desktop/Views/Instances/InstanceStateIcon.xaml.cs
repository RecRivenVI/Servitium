using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Servitium.Desktop.Views.Instances;

internal sealed partial class InstanceStateIcon : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(string), typeof(InstanceStateIcon), new PropertyMetadata(string.Empty, OnVisualPropertyChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(InstanceStateIcon), new PropertyMetadata(string.Empty, OnVisualPropertyChanged));
    public string? State { get => (string?)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    internal InstanceStateIcon() { InitializeComponent(); }
    private static void OnVisualPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) { ((InstanceStateIcon)sender).Bindings?.Update(); }
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new FrameworkElementAutomationPeer(this);
    }

    public bool IsRunning => State == "Running";
    public bool IsStopped => State == "Stopped";
    public bool IsCritical => State is "Crashed" or "Failed";
    public bool IsOffline => State == "Unreachable";
    public bool IsPending => !string.IsNullOrEmpty(State) && State is not ("Running" or "Stopped" or "Crashed" or "Failed" or "Unreachable");
}

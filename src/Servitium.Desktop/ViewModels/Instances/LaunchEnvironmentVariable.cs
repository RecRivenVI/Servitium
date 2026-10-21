using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class LaunchEnvironmentVariable(LocalizationService localization, string name = "", string value = "") : ObservableObject
{
    public string NameLabel => localization.Get("desktop.launch-variable-name");
    public string ValueLabel => localization.Get("desktop.launch-variable-value");
    public string RemoveLabel => localization.Get("desktop.launch-remove-environment");
    public string Name { get; set => SetProperty(ref field, value); } = name;
    public string Value { get; set => SetProperty(ref field, value); } = value;
    public override string ToString() { return Name; }
}

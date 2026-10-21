using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Contracts.Mods;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class ModRow(ModDetailsDto value, LocalizationService localization) : ObservableObject
{
    public ModDetailsDto Value { get; private set; } = value;
    public string Name => Value.Name;
    public string Version => Value.Version ?? localization.Get("desktop.version-not-reported");
    public bool Enabled => Value.Enabled;
    public bool CanToggle { get; internal set => SetProperty(ref field, value); }
    internal void Update(ModDetailsDto result) { Value = result; OnPropertyChanged(nameof(Enabled)); }
    internal void ResetToggle() { OnPropertyChanged(nameof(Enabled)); }
    public override string ToString() { return Name; }
}

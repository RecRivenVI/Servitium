using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class FileDirectoryTab(LocalizationService localization) : ObservableObject
{
    public string Path { get; set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(Header)); } } } = string.Empty;
    public bool CanClose { get; set => SetProperty(ref field, value); }
    public string Header => Path.Length == 0 ? localization.Get("field.working-directory") : System.IO.Path.GetFileName(Path.Replace('\\', '/'));
    public override string ToString() { return Header; }
}

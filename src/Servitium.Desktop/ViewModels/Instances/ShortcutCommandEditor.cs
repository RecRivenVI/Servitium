using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Contracts.Instances;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class ShortcutCommandEditor(LocalizationService localization, string name = "", string command = "") : ObservableObject
{
    public string Name { get; set => SetProperty(ref field, value); } = name;
    public string CommandText { get; set => SetProperty(ref field, value); } = command;
    public string NameLabel => localization.Get("field.name");
    public string CommandLabel => localization.Get("field.command");
    public string RemoveLabel => localization.Get("desktop.settings-remove-shortcut");
    internal ShortcutCommandRequest Request() { return new(Name, CommandText); }
    public override string ToString() { return Name; }
}

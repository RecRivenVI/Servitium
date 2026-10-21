using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Contracts.Scheduling;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class ScheduleActionEditor : ObservableObject
{
    private static readonly string[] ActionKinds = ["send-console-command", "delay", "start", "stop", "restart", "force-stop"];
    private readonly LocalizationService _localization;
    internal ScheduleActionEditor(LocalizationService localization, ScheduleActionDto? action = null)
    {
        _localization = localization;
        Choices = [.. ActionKinds
            .Select(kind => new SettingChoice(kind, SchedulePresentation.Action(kind, localization)))];
        Kind = action?.Kind ?? "send-console-command"; Argument = action?.Argument ?? (Kind == "send-console-command" ? "list" : string.Empty);
        ContinueOnFailure = action?.ContinueOnFailure ?? false;
    }
    public IReadOnlyList<SettingChoice> Choices { get; }
    public string ContinueLabel => _localization.Get("desktop.schedule-continue-on-failure");
    public override string ToString() { return SchedulePresentation.Action(Kind, _localization); }
    public string Kind
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) { return; }
            if (!NeedsArgument) { Argument = string.Empty; }
            OnPropertyChanged(nameof(NeedsArgument)); OnPropertyChanged(nameof(ArgumentLabel)); OnPropertyChanged(nameof(Validation));
        }
    } = "send-console-command";
    public string Argument { get; set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(Validation)); } } } = string.Empty;
    public bool ContinueOnFailure { get; set => SetProperty(ref field, value); }
    public bool NeedsArgument => Kind is "send-console-command" or "delay";
    public string ArgumentLabel => _localization.Get(Kind == "delay" ? "desktop.schedule-delay-seconds" : "field.command");
    public string Validation
    {
        get
        {
            bool valid = Kind switch
            {
                "send-console-command" => !string.IsNullOrWhiteSpace(Argument) && Argument.Length <= 4096 && !Argument.Any(char.IsControl),
                "delay" => int.TryParse(Argument, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) && seconds is >= 1 and <= 86400,
                "start" or "stop" or "restart" or "force-stop" => Argument.Length == 0,
                _ => false,
            };
            return valid ? string.Empty : _localization.Get("desktop.schedule-action-invalid");
        }
    }
    internal ScheduleActionDto Request() { return new(Kind, NeedsArgument ? Argument : null, ContinueOnFailure); }
}

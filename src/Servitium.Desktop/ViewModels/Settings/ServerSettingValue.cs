using System.Collections.Generic;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class ServerSettingValue(string group, string key, LocalizationService localization,
    int? minimum = null, int? maximum = null, IReadOnlyList<SettingChoice>? choices = null, bool secret = false) : ObservableObject
{
    private string _saved = string.Empty;
    internal string Group => group;
    internal string Key => key;
    internal bool Secret => secret;
    public string Title => localization.Get("desktop.server-setting-" + key);
    public string Description { get; private set; } = string.Empty;
    public IReadOnlyList<SettingChoice> Choices { get; } = choices ?? [];
    public string Value
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) { return; }
            OnPropertyChanged(nameof(IsOn)); OnPropertyChanged(nameof(Number));
            OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(Error));
        }
    } = string.Empty;
    public bool IsOn { get => Value == "true"; set => Value = value ? "true" : "false"; }
    public double Number { get => double.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double number) ? number : double.NaN; set => Value = value.ToString(CultureInfo.InvariantCulture); }
    public bool ClearSecret { get; set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(IsDirty)); } } }
    public bool IsDirty => secret ? ClearSecret || Value.Length > 0 : Value != _saved;
    public string Error
    {
        get
        {
            if (secret && ClearSecret && Value.Length > 0) { return localization.Get("desktop.secret-clear-conflict"); }
            return Choices.Count > 0 && !System.Linq.Enumerable.Any(Choices, choice => choice.Value == Value)
                ? localization.Format("desktop.server-setting-choice-invalid", Title)
                : minimum is not null && (!int.TryParse(Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < minimum || number > maximum)
                ? localization.Format("desktop.server-setting-invalid", Title, minimum, maximum) : string.Empty;
        }
    }
    internal string? WriteValue => secret && ClearSecret ? null : Value;
    internal void Load(string? value, string? effect)
    {
        _saved = value ?? string.Empty;
        Value = secret ? string.Empty : _saved;
        ClearSecret = false;
        Description = localization.Get(effect == "restart-required" ? "desktop.server-effect-restart" : "desktop.server-effect-immediate") +
            (secret ? " " + localization.Get(string.IsNullOrEmpty(value) ? "desktop.secret-not-set" : "desktop.secret-set") : string.Empty);
        OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(IsDirty));
    }
}

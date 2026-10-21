using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Contracts.Configurations;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class ConfigurationValue(ConfigurationFieldDto definition, LocalizationService localization) : ObservableObject
{
    private string _saved = string.Empty;
    private bool _missing;
    internal string Key => definition.Key;
    public string Title => localization.Get("config." + definition.Key + ".title");
    public string Description => localization.Get(definition.DescriptionKey) + (_missing ? " " + localization.Get("desktop.config-reference-default") : string.Empty);
    public IReadOnlyList<SettingChoice> Choices { get; } = [.. (definition.AllowedValues ?? []).Select(value =>
        new SettingChoice(value, localization.Get("config.choice." + value)))];
    public string Value
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) { return; }
            OnPropertyChanged(nameof(IsOn)); OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(Error));
        }
    } = string.Empty;
    public bool IsOn { get => Value == "true"; set => Value = value ? "true" : "false"; }
    public bool IsDirty => Value != _saved;
    public string Error
    {
        get
        {
            bool valid = definition.Type switch
            {
                "integer" => long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) &&
                    (definition.Minimum is null || number >= definition.Minimum) && (definition.Maximum is null || number <= definition.Maximum),
                "enum" => definition.AllowedValues?.Contains(Value, StringComparer.Ordinal) == true,
                "boolean" => Value is "true" or "false",
                _ => !definition.Required || !string.IsNullOrWhiteSpace(Value),
            };
            return valid ? string.Empty : localization.Format("desktop.config-value-invalid", Title, definition.Minimum, definition.Maximum);
        }
    }
    internal void Load(string? value, bool present = true)
    {
        _missing = !present; OnPropertyChanged(nameof(Description));
        _saved = value ?? definition.DefaultValue ?? string.Empty;
        Value = _saved; OnPropertyChanged(nameof(IsDirty));
    }
}

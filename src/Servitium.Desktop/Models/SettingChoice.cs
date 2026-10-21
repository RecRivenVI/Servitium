namespace Servitium.Desktop.Models;

internal sealed record SettingChoice(string Value, string Label)
{
    public override string ToString() { return Label; }
}

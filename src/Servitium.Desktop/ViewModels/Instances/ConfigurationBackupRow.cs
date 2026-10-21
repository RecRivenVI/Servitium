using System.Globalization;

using Servitium.Contracts.Configurations;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed record ConfigurationBackupRow(ConfigurationBackupDto Value)
{
    public string Label => Value.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public override string ToString() { return Label; }
}

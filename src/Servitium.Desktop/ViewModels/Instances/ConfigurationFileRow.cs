using Servitium.Contracts.Configurations;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed record ConfigurationFileRow(ConfigurationDto Value)
{
    public string Name => System.IO.Path.GetFileName(Value.RelativePath);
    public string Path => Value.RelativePath;
    public override string ToString() { return Name; }
}

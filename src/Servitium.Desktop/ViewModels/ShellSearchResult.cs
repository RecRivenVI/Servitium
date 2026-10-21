using Servitium.Contracts.Instances;

namespace Servitium.Desktop.ViewModels;

internal sealed record ShellSearchResult(string DisplayName, InstanceDto Instance, string? FilePath, string? Command)
{
    public override string ToString() { return DisplayName; }
}

using System.Collections.Generic;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed record InstanceNodeGroup(string Name, string Detail, bool IsOnline, IReadOnlyList<InstanceRow> Items)
{
    public bool IsOffline => !IsOnline;
    public override string ToString() { return Name; }
}

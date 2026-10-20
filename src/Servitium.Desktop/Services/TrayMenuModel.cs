using System;
using System.Collections.Generic;
using System.Linq;

using Servitium.Contracts.Instances;

namespace Servitium.Desktop.Services;

internal sealed record TrayInstanceMenuItem(Guid Id, string Name, string Status);

internal static class TrayMenuModel
{
    internal static IReadOnlyList<TrayInstanceMenuItem> Recent(
        IReadOnlyList<InstanceDto> instances,
        IReadOnlyList<Guid> recentIds)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(recentIds);
        var byId = instances.ToDictionary(instance => instance.Id);
        return [.. recentIds
            .Distinct()
            .Take(10)
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Select(instance => new TrayInstanceMenuItem(instance.Id, instance.Name, instance.Status))];
    }
}

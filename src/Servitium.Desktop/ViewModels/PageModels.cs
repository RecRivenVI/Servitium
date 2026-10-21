using System.Collections.Generic;

using Servitium.Contracts.Settings;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.ViewModels;

internal sealed record PageSnapshot(
    IReadOnlyDictionary<string, PageSectionSnapshot> Sections,
    IReadOnlyDictionary<string, string> Fields,
    string? StatusKey = null,
    DashboardLayoutDto? DashboardLayout = null);

internal sealed record PageSectionSnapshot(IReadOnlyList<PageListItem> Items, string? NextCursor = null);

internal sealed record PageActionResult(string? StatusKey = null, string? NavigatePageId = null, bool Refresh = true);

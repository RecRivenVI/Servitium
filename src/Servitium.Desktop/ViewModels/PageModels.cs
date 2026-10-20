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

internal sealed class TransferProgressEventArgs(
    string fileName,
    long transferredBytes,
    long totalBytes,
    double bytesPerSecond,
    System.TimeSpan? remaining,
    string state) : System.EventArgs
{
    internal string FileName { get; } = fileName;
    internal long TransferredBytes { get; } = transferredBytes;
    internal long TotalBytes { get; } = totalBytes;
    internal double BytesPerSecond { get; } = bytesPerSecond;
    internal System.TimeSpan? Remaining { get; } = remaining;
    internal string State { get; } = state;
}

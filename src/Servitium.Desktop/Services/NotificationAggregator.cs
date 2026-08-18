using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Servitium.Desktop.Services;

internal sealed record DesktopNotification(
    string Category,
    string TitleKey,
    string BodyKey,
    string PageId,
    Guid? ResourceId,
    bool Dangerous,
    DateTimeOffset OccurredAt);

internal sealed record AggregatedDesktopNotification(DesktopNotification Latest, int Count);

internal sealed class NotificationAggregator(TimeSpan window, int maximumQueued)
{
    private readonly Lock _sync = new();
    private readonly List<DesktopNotification> _queued = [];

    internal void Enqueue(DesktopNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_sync)
        {
            _queued.Add(notification);
            while (_queued.Count > maximumQueued)
            {
                int removable = _queued.FindIndex(item => !item.Dangerous);
                _queued.RemoveAt(removable >= 0 ? removable : 0);
            }
        }
    }

    internal IReadOnlyList<AggregatedDesktopNotification> TakeDue(DateTimeOffset now, bool force, bool paused = false)
    {
        lock (_sync)
        {
            if (paused)
            {
                return [];
            }

            DesktopNotification[] due = [.. _queued.Where(item => force || now - item.OccurredAt >= window)];
            if (due.Length == 0)
            {
                return [];
            }

            HashSet<DesktopNotification> selected = [.. due];
            _ = _queued.RemoveAll(selected.Contains);
            return [.. due
                .GroupBy(item => item.Category, StringComparer.Ordinal)
                .Select(group => new AggregatedDesktopNotification(group.MaxBy(item => item.OccurredAt)!, group.Count()))];
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _queued.Count;
            }
        }
    }
}

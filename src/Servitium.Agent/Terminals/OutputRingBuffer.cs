using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Servitium.Agent.Terminals;

internal sealed class OutputRingBuffer(int maximumBytes = 1024 * 1024, int maximumLines = 5000)
{
    private const string TruncatedMarker = "[Servitium: earlier output truncated]";
    private readonly Lock _gate = new();
    private readonly Queue<BufferedLine> _lines = new();
    private readonly int _maximumBytes = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private readonly int _maximumLines = maximumLines > 0 ? maximumLines : throw new ArgumentOutOfRangeException(nameof(maximumLines));
    private int _bytes;
    private bool _truncated;

    private event Action<string>? Appended;

    internal void Append(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        lock (_gate)
        {
            _lines.Enqueue(new BufferedLine(line, bytes));
            _bytes += bytes;
            while (_lines.Count > _maximumLines || _bytes > _maximumBytes)
            {
                BufferedLine removed = _lines.Dequeue();
                _bytes -= removed.Bytes;
                _truncated = true;
            }
        }

        Appended?.Invoke(line + "\r\n");
    }

    internal bool Truncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    internal IReadOnlyList<string> Subscribe(Action<string> callback, int maximumLines = 5000)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            string[] lines = [.. _lines.Select(item => item.Text).TakeLast(Math.Max(0, maximumLines))];
            Appended += callback;
            return _truncated ? [TruncatedMarker, .. lines] : lines;
        }
    }

    internal void Unsubscribe(Action<string> callback)
    {
        lock (_gate)
        {
            Appended -= callback;
        }
    }

    internal IReadOnlyList<string> Snapshot(int maximumLines = 200)
    {
        lock (_gate)
        {
            string[] lines = [.. _lines.Select(item => item.Text).TakeLast(Math.Max(0, maximumLines))];
            return _truncated ? [TruncatedMarker, .. lines] : lines;
        }
    }

    private sealed record BufferedLine(string Text, int Bytes);
}

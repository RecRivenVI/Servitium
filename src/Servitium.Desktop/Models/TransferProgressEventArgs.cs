using System;
using System.Diagnostics;

namespace Servitium.Desktop.Models;

internal sealed class TransferProgressEventArgs(
    string fileName,
    long transferredBytes,
    long totalBytes,
    double bytesPerSecond,
    TimeSpan? remaining,
    string state) : EventArgs
{
    internal string FileName { get; } = fileName;
    internal long TransferredBytes { get; } = transferredBytes;
    internal long TotalBytes { get; } = totalBytes;
    internal double BytesPerSecond { get; } = bytesPerSecond;
    internal TimeSpan? Remaining { get; } = remaining;
    internal string State { get; } = state;

    internal static TransferProgressEventArgs Create(string fileName, long completed, long total, Stopwatch stopwatch, string state)
    {
        double speed = completed / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        TimeSpan? remaining = speed <= 0 || completed >= total ? null : TimeSpan.FromSeconds((total - completed) / speed);
        return new(fileName, completed, total, speed, remaining, state);
    }
}

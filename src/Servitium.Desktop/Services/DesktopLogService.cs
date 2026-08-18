using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Servitium.Desktop.Services;

internal sealed partial class DesktopLogService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;

    internal DesktopLogService(string dataDirectory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        LogsDirectory = Path.Combine(dataDirectory, "logs");
        _ = Directory.CreateDirectory(LogsDirectory);
        RemoveExpiredLogs();
    }

    internal string LogsDirectory { get; }

    internal async Task WriteAsync(
        string level,
        string eventName,
        string? code = null,
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string path = Path.Combine(LogsDirectory, "desktop-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        string line = string.Join('\t', [
            now.ToString("O", CultureInfo.InvariantCulture),
            Clean(level),
            Clean(eventName),
            Clean(code ?? "-"),
            Clean(traceId ?? "-")]) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private void RemoveExpiredLogs()
    {
        DateTimeOffset cutoff = _timeProvider.GetUtcNow().AddDays(-14);
        foreach (string file in Directory.EnumerateFiles(LogsDirectory, "desktop-????????.log", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string date = name["desktop-".Length..];
            if (DateTimeOffset.TryParseExact(
                date,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed) && parsed < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private static string Clean(string value)
    {
        return new([.. value.Where(character => character is not ('\r' or '\n' or '\t')).Take(256)]);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}

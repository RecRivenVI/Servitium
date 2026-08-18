using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Desktop.Models;

namespace Servitium.Desktop.Services;

internal sealed partial class DesktopSettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal DesktopSettingsService(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Servitium", "Desktop");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
    }

    internal string DataDirectory { get; }

    internal string SettingsPath { get; }

    internal DesktopSettings Current { get; private set; } = Defaults();

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(SettingsPath))
        {
            return;
        }

        FileStream stream = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            DesktopSettings loaded = await JsonSerializer.DeserializeAsync<DesktopSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? Defaults();
            // 旧版本曾用 null 表示“直到重启”，但该值会跨进程恢复；加载时必须使这种进程态失效。
            Current = loaded.NotificationsPaused && loaded.NotificationsPausedUntil is null
                ? loaded with { NotificationsPaused = false }
                : loaded;
        }
    }

    internal async Task UpdateAsync(Func<DesktopSettings, DesktopSettings> update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DesktopSettings next = update(Current) ?? throw new InvalidOperationException("The settings update returned no value.");
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async Task WriteAsync(DesktopSettings settings, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(DataDirectory);
        string temporary = SettingsPath + ".tmp";
        FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, SettingsPath, overwrite: true);
        Current = settings;
    }

    private static DesktopSettings Defaults()
    {
        return new(
        [new ServerProfile(Guid.Parse("00000000-0000-4000-8000-000000000001"), "127.0.0.1", new Uri("http://127.0.0.1:5344"), DateTimeOffset.UnixEpoch)],
        Guid.Parse("00000000-0000-4000-8000-000000000001"),
        "zh-Hans",
        "system",
        true,
        false,
        null,
        []);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}

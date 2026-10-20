using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

using Servitium.Client.Realtime;
using Servitium.Contracts.Events;

namespace Servitium.Desktop.Services;

internal sealed partial class DesktopNotificationService : IDisposable
{
    internal static readonly IReadOnlyList<string> Categories =
    [
        "instance-crashed",
        "auto-restart-exhausted",
        "node-offline",
        "node-online",
        "operation-completed",
        "operation-failed",
        "hmcl-published",
        "hmcl-integrity-alert",
    ];

    private readonly SessionService _session;
    private readonly DesktopSettingsService _settings;
    private readonly LocalizationService _localization;
    private readonly DesktopLogService _log;
    private readonly TimeProvider _timeProvider;
    private readonly NotificationAggregator _aggregator = new(TimeSpan.FromSeconds(60), 50);
    private readonly NotificationPauseState _pauseState = new();
    private readonly ITimer _timer;
    private EventsConnection? _events;
    private bool _registered;
    private bool _disposed;

    internal DesktopNotificationService(
        SessionService session,
        DesktopSettingsService settings,
        LocalizationService localization,
        DesktopLogService log,
        TimeProvider timeProvider)
    {
        _session = session;
        _settings = settings;
        _localization = localization;
        _log = log;
        _timeProvider = timeProvider;
        _timer = timeProvider.CreateTimer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal void Start()
    {
        if (!_session.IsConnected)
        {
            return;
        }

        if (!_registered)
        {
            try
            {
                AppNotificationManager.Default.Register();
                _registered = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or COMException)
            {
                _ = _log.WriteAsync("warning", "notification.registration-failed", exception.GetType().Name);
            }
        }

        EventsConnection current = _session.Client.Events;
        if (ReferenceEquals(_events, current))
        {
            return;
        }

        _events?.EventReceived -= OnEventReceived;

        _events = current;
        _events.EventReceived += OnEventReceived;
    }

    internal void ResumeAndFlush()
    {
        _pauseState.ClearProcessPause();
        if (!IsCurrentlyPaused())
        {
            Flush(force: true);
        }
    }

    internal void PauseUntilRestart()
    {
        _pauseState.PauseUntilRestart();
    }

    internal void ClearProcessPause()
    {
        _pauseState.ClearProcessPause();
    }

    internal bool IsCurrentlyPaused()
    {
        return _pauseState.IsPaused(_settings.Current, _timeProvider.GetUtcNow());
    }

    internal static bool IsPaused(Models.DesktopSettings settings, DateTimeOffset now)
    {
        return settings.NotificationsPaused &&
            settings.NotificationsPausedUntil is DateTimeOffset until &&
            until > now;
    }

    private void OnEventReceived(object? sender, RealtimeEventArgs args)
    {
        DesktopNotification? notification = Map(args.Value);
        if (notification is not null && IsEnabled(notification.Category))
        {
            _aggregator.Enqueue(notification);
        }
    }

    private bool IsEnabled(string category)
    {
        return _settings.Current.NotificationPreferences?.TryGetValue(category, out bool enabled) == true
            ? enabled
            : DefaultEnabled(category);
    }

    internal static bool DefaultEnabled(string category)
    {
        return category is "instance-crashed" or "auto-restart-exhausted" or "node-offline" or
        "operation-failed" or "hmcl-published" or "hmcl-integrity-alert";
    }

    private static DesktopNotification? Map(RealtimeEventDto value)
    {
        return value.Type switch
        {
            EventTypes.InstanceCrashed => Create(value, "instance-crashed", "PG-012", ReadGuid(value.Payload, "instanceId"), true),
            EventTypes.InstanceAutoRestartExhausted => Create(value, "auto-restart-exhausted", "PG-012", ReadGuid(value.Payload, "instanceId"), true),
            EventTypes.NodeStatusChanged when IsState(value.Payload, "status", "Offline") =>
                Create(value, "node-offline", "PG-021", ReadGuid(value.Payload, "nodeId"), true),
            EventTypes.NodeStatusChanged when IsState(value.Payload, "status", "Online") =>
                Create(value, "node-online", "PG-021", ReadGuid(value.Payload, "nodeId"), false),
            EventTypes.OperationCompleted when IsState(value.Payload, "status", "Failed") =>
                Create(value, "operation-failed", "PG-044", ReadGuid(value.Payload, "operationId"), true),
            EventTypes.OperationCompleted => Create(value, "operation-completed", "PG-044", ReadGuid(value.Payload, "operationId"), false),
            EventTypes.HmclPublished => Create(value, "hmcl-published", "PG-061", ReadGuid(value.Payload, "channelId"), false),
            EventTypes.HmclIntegrityAlert => Create(value, "hmcl-integrity-alert", "PG-061", ReadGuid(value.Payload, "channelId"), true),
            _ => null,
        };
    }

    private static DesktopNotification Create(RealtimeEventDto value, string category, string pageId, Guid? id, bool dangerous)
    {
        return new(category, "notification." + category + ".title", "notification." + category + ".body", pageId, id, dangerous, value.OccurredAt);
    }

    private void Flush(bool force = false)
    {
        if (_disposed || !_registered || IsSuppressedByWindows())
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (AggregatedDesktopNotification item in _aggregator.TakeDue(now, force, _pauseState.IsPaused(_settings.Current, now)))
        {
            string body = _localization.Get(item.Latest.BodyKey);
            if (item.Count > 1)
            {
                body = item.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) + " · " + body;
            }

            AppNotificationBuilder builder = new AppNotificationBuilder()
                .AddText(_localization.Get(item.Latest.TitleKey))
                .AddText(body)
                .AddArgument("page", item.Latest.PageId);
            if (item.Latest.ResourceId is Guid resourceId)
            {
                _ = builder.AddArgument("resource", resourceId.ToString("D"));
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
    }

    private static Guid? ReadGuid(JsonElement payload, string name)
    {
        return payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        value.TryGetGuid(out Guid parsed) ? parsed : null;
    }

    private static string? ReadString(JsonElement payload, string name)
    {
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;
    }

    private static bool IsState(JsonElement payload, string name, string expected)
    {
        return string.Equals(ReadString(payload, name), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuppressedByWindows()
    {
        int result = QueryNotificationState(out NotificationState state);
        return result == 0 && state != NotificationState.AcceptsNotifications;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        _events?.EventReceived -= OnEventReceived;
        _events = null;

        if (_registered)
        {
            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }

    private enum NotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningDirect3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryUserNotificationState")]
    private static partial int QueryNotificationState(out NotificationState state);
}

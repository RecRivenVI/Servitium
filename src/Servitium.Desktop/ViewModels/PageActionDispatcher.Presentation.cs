using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Distribution;
using Servitium.Contracts.Files;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Scheduling;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.ViewModels;

internal sealed partial class PageActionDispatcher
{
    private InstanceDto RequireInstance()
    {
        return services.Session.SelectedInstance ?? throw SelectionRequired();
    }

    private NodeDto RequireNode()
    {
        return services.Session.SelectedNode ?? throw SelectionRequired();
    }

    private HmclChannelDto RequireChannel()
    {
        return services.Session.SelectedChannel ?? throw SelectionRequired();
    }

    private Contracts.Identity.UserDto RequireUser()
    {
        return services.Session.SelectedUser ?? throw SelectionRequired();
    }

    private InvalidOperationException SelectionRequired()
    {
        return new(services.Localization.Get("common.not-selected"));
    }

    private static string Value(IReadOnlyDictionary<string, string> fields, string key, string defaultValue = "")
    {
        return fields.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : defaultValue;
    }

    private static string[] Split(IReadOnlyDictionary<string, string> fields, string key)
    {
        return Value(fields, key).Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private int Integer(IReadOnlyDictionary<string, string> fields, string key, int defaultValue)
    {
        string value = Value(fields, key);
        return value.Length == 0
            ? defaultValue
            : !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? throw new ArgumentException(services.Localization.Get("error.validation-failed"))
            : result;
    }

    private static bool Boolean(IReadOnlyDictionary<string, string> fields, string key, bool defaultValue)
    {
        return bool.TryParse(Value(fields, key), out bool value) ? value : defaultValue;
    }

    private long LongInteger(IReadOnlyDictionary<string, string> fields, string key, long defaultValue)
    {
        string value = Value(fields, key);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
                ? throw new ArgumentException(services.Localization.Get("error.validation-failed"))
                : result;
    }

    private int? NullableInteger(IReadOnlyDictionary<string, string> fields, string key)
    {
        string value = Value(fields, key);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : throw new ArgumentException(services.Localization.Get("error.validation-failed"));
    }

    private string CombineRelative(string parent, string child)
    {
        if (Path.IsPathRooted(child) || child.Split(['/', '\\']).Any(segment => segment == ".."))
        {
            throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        }

        string normalizedParent = parent.Replace('\\', '/').Trim('/');
        string normalizedChild = child.Replace('\\', '/').Trim('/');
        return normalizedParent.Length == 0 ? normalizedChild : normalizedParent + "/" + normalizedChild;
    }

    private FileStream OpenUpload(string path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? throw new ArgumentException(services.Localization.Get("error.not-found"))
            : new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task<OperationAcceptedDto> UploadFileAsync(
        InstanceDto instance,
        string sourcePath,
        string destinationPath,
        string? modKind,
        CancellationToken cancellationToken,
        bool overwrite = false)
    {
        const int partSize = 8 * 1024 * 1024;
        FileStream source = OpenUpload(sourcePath);
        await using (source.ConfigureAwait(false))
        {
            var stopwatch = Stopwatch.StartNew();
            ReportTransfer(Path.GetFileName(sourcePath), 0, source.Length, stopwatch, "uploading");
            UploadSessionDto session = await services.Session.Client.CreateFileUploadAsync(
                instance.Id,
                instance.NodeId,
                new CreateUploadSessionRequest(Path.GetFileName(sourcePath), destinationPath, source.Length, partSize, overwrite),
                cancellationToken).ConfigureAwait(false);

            try
            {
                using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[partSize];
                for (int index = 0; ; index++)
                {
                    await _transferPause.WaitAsync(cancellationToken).ConfigureAwait(false);
                    int length = await ReadPartAsync(source, buffer, cancellationToken).ConfigureAwait(false);
                    if (length == 0)
                    {
                        break;
                    }

                    fullHash.AppendData(buffer, 0, length);
                    string partHash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length)));
                    MemoryStream part = new(buffer, 0, length, writable: false, publiclyVisible: true);
                    _ = await services.Session.Client.PutFileUploadPartAsync(
                        instance.Id, instance.NodeId, session.Id, index, partHash, part, length, cancellationToken).ConfigureAwait(false);
                    ReportTransfer(Path.GetFileName(sourcePath), source.Position, source.Length, stopwatch, "uploading");
                }

                string sha256 = Convert.ToHexString(fullHash.GetHashAndReset());
                OperationAcceptedDto result = modKind is null
                    ? await services.Session.Client.CompleteFileUploadAsync(
                        instance.Id, instance.NodeId, session.Id, new CompleteUploadRequest(sha256), cancellationToken).ConfigureAwait(false)
                    : await services.Session.Client.CompleteModUploadAsync(
                        instance.Id, instance.NodeId, new UploadModRequest(session.Id, modKind, sha256), cancellationToken).ConfigureAwait(false);
                ReportTransfer(Path.GetFileName(sourcePath), source.Length, source.Length, stopwatch, "succeeded");
                return result;
            }
            catch (Exception uploadError)
            {
                ReportTransfer(
                    Path.GetFileName(sourcePath),
                    source.Position,
                    source.Length,
                    stopwatch,
                    cancellationToken.IsCancellationRequested ? "cancelled" : "failed");
                try
                {
                    await services.Session.Client.CancelFileUploadAsync(
                        instance.Id, instance.NodeId, session.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(uploadError, cleanupError);
                }

                throw;
            }
        }
    }

    private static async Task<int> ReadPartAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await source.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private async Task<T> UploadStreamAsync<T>(
        string path,
        Func<Stream, CancellationToken, Task<T>> upload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        FileStream source = OpenUpload(path);
        await using (source.ConfigureAwait(false))
        {
            var stopwatch = Stopwatch.StartNew();
            string fileName = Path.GetFileName(path);
            ReportTransfer(fileName, 0, source.Length, stopwatch, "uploading");
            using ProgressReadStream progress = new(
                source,
                _transferPause,
                completed => ReportTransfer(fileName, completed, source.Length, stopwatch, "uploading"));
            try
            {
                T result = await upload(progress, cancellationToken).ConfigureAwait(false);
                ReportTransfer(fileName, source.Length, source.Length, stopwatch, "succeeded");
                return result;
            }
            catch
            {
                ReportTransfer(
                    fileName,
                    source.Position,
                    source.Length,
                    stopwatch,
                    cancellationToken.IsCancellationRequested ? "cancelled" : "failed");
                throw;
            }
        }
    }

    internal async Task UploadDroppedAsync(
        string pageId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(paths);
        if (pageId is not ("PG-013" or "PG-016"))
        {
            throw new ArgumentOutOfRangeException(nameof(pageId));
        }

        InstanceDto instance = RequireInstance();
        foreach ((string source, string relative) in EnumerateDroppedFiles(paths))
        {
            string? modKind = pageId == "PG-016" ? "mods" : null;
            string destinationRoot = modKind ?? _directoryPath;
            _ = await UploadFileAsync(instance, source, CombineRelative(destinationRoot, relative), modKind, cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<(string Source, string Relative)> EnumerateDroppedFiles(IReadOnlyList<string> paths)
    {
        List<(string Source, string Relative)> result = [];
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                result.Add((path, Path.GetFileName(path)));
                continue;
            }

            DirectoryInfo root = new(path);
            if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("A dropped path is invalid or is a reparse point.", nameof(paths));
            }

            Stack<DirectoryInfo> pending = new();
            pending.Push(root);
            while (pending.TryPop(out DirectoryInfo? directory))
            {
                foreach (FileInfo file in directory.EnumerateFiles())
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new ArgumentException("A dropped file is a reparse point.", nameof(paths));
                    }

                    result.Add((file.FullName, Path.Combine(root.Name, Path.GetRelativePath(root.FullName, file.FullName))));
                }

                foreach (DirectoryInfo child in directory.EnumerateDirectories())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new ArgumentException("A dropped directory is a reparse point.", nameof(paths));
                    }

                    pending.Push(child);
                }
            }
        }

        return result;
    }

    private void ReportTransfer(
        string fileName,
        long completed,
        long total,
        Stopwatch stopwatch,
        string state)
    {
        double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        double speed = completed / seconds;
        TimeSpan? remaining = speed <= 0 || completed >= total
            ? null
            : TimeSpan.FromSeconds((total - completed) / speed);
        TransferProgressChanged?.Invoke(
            this,
            new TransferProgressEventArgs(fileName, completed, total, speed, remaining, state));
    }

    private sealed class TransferPauseGate
    {
        private readonly Lock _sync = new();
        private TaskCompletionSource? _resume;

        internal void Pause()
        {
            lock (_sync)
            {
                _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal void Resume()
        {
            TaskCompletionSource? resume;
            lock (_sync)
            {
                resume = _resume;
                _resume = null;
            }

            _ = (resume?.TrySetResult());
        }

        internal Task WaitAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return _resume?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
            }
        }
    }

    private sealed partial class ProgressReadStream(
        Stream inner,
        TransferPauseGate pause,
        Action<long> report) : Stream
    {
        private long _lastReported;
        private long _lastReportTick;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            OnRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await pause.WaitAsync(cancellationToken).ConfigureAwait(false);
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            OnRead(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private void OnRead(int read)
        {
            if (read <= 0)
            {
                return;
            }

            long position = inner.Position;
            long now = Environment.TickCount64;
            if (position == inner.Length || now - _lastReportTick >= 100 || position - _lastReported >= 1024 * 1024)
            {
                _lastReported = position;
                _lastReportTick = now;
                report(position);
            }
        }
    }

    private sealed partial class ProgressWriteStream(
        Stream inner,
        TransferPauseGate pause,
        Action<long> report) : Stream
    {
        private long _written;
        private long _lastReported;
        private long _lastReportTick;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            OnWrite(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await pause.WaitAsync(cancellationToken).ConfigureAwait(false);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            OnWrite(buffer.Length);
        }

        private void OnWrite(int count)
        {
            _written += count;
            long now = Environment.TickCount64;
            if (now - _lastReportTick >= 100 || _written - _lastReported >= 1024 * 1024)
            {
                _lastReported = _written;
                _lastReportTick = now;
                report(_written);
            }
        }
    }

    private UpsertScheduledTaskRequest ScheduleRequest(IReadOnlyDictionary<string, string> fields, long? revision)
    {
        string triggerText = Value(fields, "trigger", "interval:300");
        if (!TryParseScheduleTrigger(triggerText, out ScheduleTriggerDto? trigger))
        {
            throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        }

        ScheduleActionDto[] actions = [.. Value(fields, "actions", "command:say Servitium")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Select(parts => new ScheduleActionDto(parts[0], parts.Length == 2 ? parts[1] : null, false))];
        return actions.Length == 0
            ? throw new ArgumentException(services.Localization.Get("error.validation-failed"))
            : new UpsertScheduledTaskRequest(
            Value(fields, "name"),
            trigger!,
            actions,
            Integer(fields, "remaining-runs", -1),
            Value(fields, "overlap-policy", "skip"),
            Value(fields, "enabled", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            revision);
    }

    internal static bool TryParseScheduleTrigger(string value, out ScheduleTriggerDto? trigger)
    {
        trigger = null;
        string[] triggerParts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (triggerParts.Length != 2)
        {
            return false;
        }

        if (triggerParts[0] == "interval" &&
            int.TryParse(triggerParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
        {
            trigger = new("interval", seconds, null, null, null);
            return true;
        }

        if (triggerParts[0] == "specified" &&
            DateTimeOffset.TryParse(triggerParts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at))
        {
            trigger = new("specified", null, null, null, at.ToUniversalTime());
            return true;
        }

        if (triggerParts[0] != "calendar")
        {
            return false;
        }

        string[] calendarParts = triggerParts[1].Split('@', 2, StringSplitOptions.TrimEntries);
        if (calendarParts.Length != 2 ||
            !TimeOnly.TryParseExact(calendarParts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly localTime))
        {
            return false;
        }

        string[] dayParts = calendarParts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dayParts.Length == 0)
        {
            return false;
        }

        List<int> days = [];
        foreach (string dayPart in dayParts)
        {
            if (!int.TryParse(dayPart, NumberStyles.None, CultureInfo.InvariantCulture, out int day) ||
                day is < 0 or > 6 || days.Contains(day))
            {
                return false;
            }

            days.Add(day);
        }

        trigger = new("calendar", null, days, localTime, null);
        return true;
    }

    private static string FormatScheduleTrigger(ScheduleTriggerDto trigger)
    {
        return trigger.Kind switch
        {
            "interval" => "interval:" + (trigger.IntervalSeconds ?? 300).ToString(CultureInfo.InvariantCulture),
            "calendar" => "calendar:" + string.Join(',', trigger.DaysOfWeek ?? []) + "@" + (trigger.LocalTime ?? new TimeOnly(0, 0)).ToString("HH:mm", CultureInfo.InvariantCulture),
            "specified" => "specified:" + trigger.SpecifiedAt?.ToString("O", CultureInfo.InvariantCulture),
            _ => trigger.Kind,
        };
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> fields, string key, int fallback)
    {
        return int.TryParse(Value(fields, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static UpdateInstanceRequest DefaultInstanceConfiguration(InstanceDetailsDto details)
    {
        return new(
            details.Instance.Name,
            details.Instance.Tags,
            details.Notes,
            details.Launch,
            "stop",
            60,
            false,
            false,
            3,
            new RconSettingsRequest(false, 25575, null),
            new QuerySettingsRequest(false, "127.0.0.1", 25565));
    }

    private StructuredLaunchRequest CreateLaunch(IReadOnlyDictionary<string, string> fields)
    {
        if (!Guid.TryParse(Value(fields, "java-runtime"), out Guid runtimeId))
        {
            throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        }

        int minimumHeap = Integer(fields, "minimum-heap", 1024);
        int maximumHeap = Integer(fields, "maximum-heap", 2048);
        string targetKind = Value(fields, "target-kind", "jar");
        string target = Value(fields, "launch", "server.jar");
        bool advanced = Boolean(fields, "advanced-mode", false);
        return minimumHeap < 128 || maximumHeap < minimumHeap || target.Length == 0 || targetKind is not ("jar" or "argfile")
            ? throw new ArgumentException(services.Localization.Get("error.validation-failed"))
            : new StructuredLaunchRequest(
            runtimeId,
            minimumHeap,
            maximumHeap,
            Split(fields, "jvm-arguments"),
            targetKind,
            target,
            Split(fields, "server-arguments"),
            ParseKeyValues(Value(fields, "environment-variables")).ToDictionary(
                pair => pair.Key,
                pair => pair.Value ?? string.Empty,
                StringComparer.Ordinal),
            advanced,
            advanced ? Split(fields, "advanced-arguments") : null);
    }

    private static CreateHmclReleaseMetadataRequest Metadata(
        IReadOnlyDictionary<string, string> fields,
        string defaultGameVersion = "1.21.1")
    {
        return new(
            Value(fields, "name"),
            Value(fields, "author"),
            Value(fields, "version", "1.0.0"),
            Value(fields, "description"),
            Value(fields, "game-version", defaultGameVersion),
            NullIfEmpty(Value(fields, "loader-id")),
            NullIfEmpty(Value(fields, "loader-version")),
            NullIfEmpty(Value(fields, "optifine-version")));
    }

    private List<PageListItem> DifferenceItems(HmclPublishDiffDto difference, object value)
    {
        List<PageListItem> items = [];
        items.AddRange(difference.Added.Select(path => Item(path, services.Localization.Get("hmcl.preview.added"), value)));
        items.AddRange(difference.Replaced.Select(path => Item(path, services.Localization.Get("hmcl.preview.replaced"), value)));
        items.AddRange(difference.Deleted.Select(path => Item(path, services.Localization.Get("hmcl.preview.deleted"), value)));
        if (difference.LoaderChanged)
        {
            items.Add(Item(services.Localization.Get("hmcl.preview.loader-changed"), null, value));
        }

        return items;
    }

    private static (string? Id, string? Version) ParseLoader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        string[] parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (parts[0], null);
    }

    private Client.ActivityQuery ActivityFilter(IReadOnlyDictionary<string, string> fields)
    {
        return new(
            Value(fields, "actor"),
            Value(fields, "action"),
            ResourceFilter(fields),
            Value(fields, "outcome"),
            DateFilter(fields, "from"),
            DateFilter(fields, "to"));
    }

    private Client.SecurityAuditQuery SecurityFilter(IReadOnlyDictionary<string, string> fields)
    {
        return new(
            Value(fields, "actor"),
            Value(fields, "category"),
            Value(fields, "action"),
            Value(fields, "resource"),
            Value(fields, "outcome"),
            DateFilter(fields, "from"),
            DateFilter(fields, "to"));
    }

    private static string ResourceFilter(IReadOnlyDictionary<string, string> fields)
    {
        string objectType = Value(fields, "object-type");
        string resource = Value(fields, "resource");
        return objectType.Length == 0
            ? resource
            : objectType + ":" + resource;
    }

    private DateTimeOffset? DateFilter(IReadOnlyDictionary<string, string> fields, string key)
    {
        string value = Value(fields, key);
        return value.Length == 0
            ? null
            : DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : throw new ArgumentException(services.Localization.Get("error.validation-failed"));
    }

    private static string? NullIfEmpty(string value)
    {
        return value.Length == 0 ? null : value;
    }

    private string DisplayValue(string category, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return services.Localization.Get("common.unknown");
        }

        string normalized = StableValueKey(value);
        string key = category == "status" ? "state." + normalized : "value." + category + "." + normalized;
        string localized = services.Localization.Get(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? services.Localization.Get("common.unknown")
            : localized;
    }

    private static string StableValueKey(string value)
    {
        System.Text.StringBuilder key = new(value.Length + 8);
        foreach (char character in value.Trim())
        {
            if (char.IsUpper(character) && key.Length > 0 && key[^1] != '-')
            {
                _ = key.Append('-');
            }

            _ = key.Append(character is '_' or ' ' ? '-' : char.ToLowerInvariant(character));
        }

        return key.ToString();
    }

    private Dictionary<string, string?> ParseKeyValues(string input)
    {
        Dictionary<string, string?> result = new(StringComparer.Ordinal);
        foreach (string line in input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new ArgumentException(services.Localization.Get("error.validation-failed"));
            }

            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return result;
    }

    private async Task<PageActionResult> ExportAsync(
        string suggestedName,
        string extension,
        Func<Stream, CancellationToken, Task> export,
        CancellationToken cancellationToken)
    {
        string? path = await services.Interaction.PickSaveFileAsync(suggestedName, extension).ConfigureAwait(true);
        if (path is null)
        {
            return new("state.cancelled", null, false);
        }

        FileStream destination = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (destination.ConfigureAwait(false))
        {
            await export(destination, cancellationToken).ConfigureAwait(false);
        }
        return new("common.operation-completed", null, false);
    }
}

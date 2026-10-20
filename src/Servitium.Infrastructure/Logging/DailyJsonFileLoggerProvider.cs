using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

using Servitium.Application.Abstractions;

namespace Servitium.Infrastructure.Logging;

/// <summary>写入按 UTC 日期滚动的 UTF-8 JSON Lines 服务日志。</summary>
public sealed class DailyJsonFileLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _serviceName;
    private readonly IClock _clock;
    private StreamWriter? _writer;
    private DateOnly? _writerDate;
    private bool _disposed;

    /// <summary>创建文件日志提供程序。</summary>
    public DailyJsonFileLoggerProvider(string directory, string serviceName, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(clock);
        _directory = Path.GetFullPath(directory);
        _serviceName = serviceName;
        _clock = clock;
        _ = Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return new DailyJsonFileLogger(this, categoryName);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "日志提供程序不能因磁盘或序列化故障递归终止业务请求；健康与磁盘监控负责暴露持久化故障。")]
    private void Write(LogLevel level, EventId eventId, string category, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                DateTimeOffset now = _clock.UtcNow;
                var date = DateOnly.FromDateTime(now.UtcDateTime);
                if (_writer is null || _writerDate != date)
                {
                    _writer?.Dispose();
                    string path = Path.Combine(
                        _directory,
                        _serviceName + "-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024) { AutoFlush = true };
                    _writerDate = date;
                }

                string line = JsonSerializer.Serialize(new
                {
                    timestamp = now,
                    level = level.ToString(),
                    category,
                    eventId = eventId.Id,
                    message = SecretRedactor.Redact(message),
                    exception = exception is null ? null : SecretRedactor.Redact(exception.ToString()),
                });
                _writer.WriteLine(line);
            }
        }
        catch (Exception)
        {
            // 日志写入路径不得再次调用 ILogger，否则会递归；请求遥测仍会记录失败计数。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }

        GC.SuppressFinalize(this);
    }

    private sealed class DailyJsonFileLogger(DailyJsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, eventId, category, formatter(state, exception), exception);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

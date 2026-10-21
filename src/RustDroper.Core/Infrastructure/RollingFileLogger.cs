using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RustDroper.Core.Infrastructure;

/// <summary>
/// Günlük dosyaya yazan hafif <see cref="ILoggerProvider"/>: <c>rustdroper-yyyyMMdd.log</c>, gün değişince yeni dosya,
/// <paramref name="retainDays"/> günden eski dosyalar silinir. Her satır <see cref="SecretMasker"/>'dan geçer.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const string FilePrefix = "rustdroper-";
    private const string FileExtension = ".log";

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly int _retainDays;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    public RollingFileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retainDays = 7, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _minimumLevel = minimumLevel;
        _retainDays = Math.Max(1, retainDays);
        _time = timeProvider ?? TimeProvider.System;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = _time.GetLocalNow();
        var builder = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        var line = SecretMasker.Apply(builder.ToString());
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter(DateOnly.FromDateTime(now.DateTime));
                _writer!.WriteLine(line);
            }
            catch (IOException)
            {
                // Günlük yazılamıyorsa uygulama çökmez; sessizce atlanır.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void EnsureWriter(DateOnly today)
    {
        if (_writer is not null && _writerDate == today)
        {
            return;
        }

        _writer?.Dispose();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{FilePrefix}{today:yyyyMMdd}{FileExtension}");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true,
        };
        _writerDate = today;
        DeleteOldFiles(today);
    }

    private void DeleteOldFiles(DateOnly today)
    {
        var cutoff = today.AddDays(-_retainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
        {
            var datePart = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}

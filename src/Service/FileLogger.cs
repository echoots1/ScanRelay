using System.Collections.Concurrent;
using System.Text;

namespace ScanRelay.Service;

/// <summary>A rolling log file in the data folder: one file per day, kept for 30 days.</summary>
public sealed class FileLoggerProvider(string folder) : ILoggerProvider
{
    private const int KeepDays = 30;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private DateOnly _cleaned;

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        var now = DateTime.Now;
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"scanrelay-{now:yyyyMMdd}.log"), line, Encoding.UTF8);
                var today = DateOnly.FromDateTime(now);
                if (_cleaned == today) return;
                _cleaned = today;
                foreach (var old in Directory.EnumerateFiles(folder, "scanrelay-*.log"))
                {
                    if (File.GetLastWriteTime(old) < now.AddDays(-KeepDays)) File.Delete(old);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop the service.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "FATAL",
                _ => logLevel.ToString().ToUpperInvariant(),
            };
            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var text = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ')
                .Append(shortCategory).Append(": ").AppendLine(formatter(state, exception));
            if (exception is not null) text.AppendLine(exception.ToString());
            provider.Write(text.ToString());
        }
    }
}

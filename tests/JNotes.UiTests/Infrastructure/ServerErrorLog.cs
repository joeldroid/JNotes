using Microsoft.Extensions.Logging;

namespace JNotes.UiTests.Infrastructure;

/// <summary>
/// Error-and-above log entries written by the in-process app, so an unhandled circuit exception fails the
/// test that caused it even when the page happens to look fine.
/// </summary>
public sealed class ServerErrorLog
{
    private readonly List<string> _entries = [];
    private readonly object _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public IReadOnlyList<string> Since(int mark)
    {
        lock (_gate)
        {
            return _entries.Skip(mark).ToList();
        }
    }

    internal void Add(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }
}

internal sealed class ServerErrorLogProvider(ServerErrorLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, log);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ServerErrorLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            log.Add($"{category}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}".TrimEnd());
        }
    }
}

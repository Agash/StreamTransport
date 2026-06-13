using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// A logger factory that captures the library's diagnostics into a timestamped, thread-safe buffer so a test
/// can dump exactly how far each peer's handshake and media flow got when something stalls. Debug and above.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, _clock);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    public string Dump() => _entries.IsEmpty ? "(no log entries)" : string.Join(Environment.NewLine, _entries);

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries, Stopwatch clock) : ILogger
    {
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string line = $"{clock.ElapsedMilliseconds,6}ms [{logLevel.ToString()[0]}] {_shortCategory}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += $" || {exception.GetType().Name}: {exception.Message}";
            }

            entries.Enqueue(line);
        }
    }
}

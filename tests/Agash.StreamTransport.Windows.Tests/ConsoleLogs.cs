using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Windows.Tests;

// Logs to the console at a chosen level, for a test whose pipeline is otherwise silent about what it
// dropped. The test runner shows the output of failed tests.
internal sealed class ConsoleLogs(LogLevel minimum) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, minimum);

    public void Dispose() { }

    private sealed class Logger(string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (IsEnabled(logLevel))
            {
                Console.WriteLine(
                    $"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : $" ({exception})")}"
                );
            }
        }
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace StreamTransport.Agent;

/// <summary>
/// An <see cref="ILoggerProvider"/> that renders log messages through Spectre.Console, so the host's
/// structured logging (from the transport library and the agent) shares the same styled console as the
/// rest of the UI instead of the plain console logger.
/// </summary>
internal sealed class SpectreLoggerProvider(LogLevel minLevel = LogLevel.Information) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, SpectreLogger> _loggers = new(StringComparer.Ordinal);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new SpectreLogger(name, minLevel));

    public void Dispose() => _loggers.Clear();

    private sealed class SpectreLogger(string category, LogLevel minLevel) : ILogger
    {
        // Show the short type name, not the full namespace, to keep lines readable.
        private readonly string _shortCategory = category.Contains('.', StringComparison.Ordinal)
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string colour = logLevel switch
            {
                LogLevel.Critical or LogLevel.Error => "red",
                LogLevel.Warning => "yellow",
                LogLevel.Information => "grey",
                _ => "grey50",
            };

            AnsiConsole.MarkupLineInterpolated($"[{colour}]{Tag(logLevel)} {_shortCategory}: {formatter(state, exception)}[/]");
            if (exception is not null)
            {
                // Spectre's rich exception formatter uses dynamic code (not NativeAOT-safe); fall back to a
                // plain dump when AOT-compiled.
                if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                {
                    AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
                }
                else
                {
                    AnsiConsole.WriteLine(exception.ToString());
                }
            }
        }

        private static string Tag(LogLevel level) => level switch
        {
            LogLevel.Critical => "crit",
            LogLevel.Error => "fail",
            LogLevel.Warning => "warn",
            LogLevel.Information => "info",
            LogLevel.Debug => "dbug",
            _ => "trce",
        };
    }
}

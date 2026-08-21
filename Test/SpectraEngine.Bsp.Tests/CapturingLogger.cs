using Microsoft.Extensions.Logging;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// An <see cref="ILogger"/> that records every formatted message so tests can
/// assert on logging behaviour — e.g. that a defective static-world snapshot
/// is reported exactly once instead of throwing or spamming every frame.
/// Thread-safe because the logger is shared with code under test that also
/// runs work on the thread pool.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly object _lock = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    /// <summary>All messages logged at <paramref name="level"/>, in order.</summary>
    public IReadOnlyList<string> MessagesAt(LogLevel level)
    {
        lock (_lock)
            return _entries.Where(e => e.Level == level).Select(e => e.Message).ToArray();
    }

    /// <summary>Everything captured so far, one line per entry — for failure diagnostics.</summary>
    public string Describe()
    {
        lock (_lock)
            return _entries.Count == 0
                ? "(no log entries)"
                : string.Join(Environment.NewLine, _entries.Select(e => $"[{e.Level}] {e.Message}"));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
            _entries.Add((logLevel, formatter(state, exception)));
    }
}

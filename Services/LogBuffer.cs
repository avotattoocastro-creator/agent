namespace AvoTelemetryAgent.Services;

/// <summary>
/// A single log entry stored in the ring buffer.
/// </summary>
public sealed record LogEntry(
    DateTime   TimestampUtc,
    string     Level,
    string     Category,
    string     Message);

/// <summary>
/// Thread-safe ring buffer that retains the last 5000 log entries.
/// Also acts as an ILoggerProvider so all .NET log calls are captured.
/// </summary>
public sealed class LogBuffer : ILoggerProvider
{
    private const int Capacity = 5000;

    private readonly LogEntry?[] _ring = new LogEntry[Capacity];
    private int  _head  = 0;
    private int  _count = 0;
    private readonly object _lock = new();

    // ── Live-stream hook ───────────────────────────────────────────────────────

    /// <summary>
    /// Raised for every new entry, outside the internal lock.
    /// Subscribers must be fast and non-blocking.
    /// </summary>
    public event Action<LogEntry>? OnEntry;

    // ── Write ──────────────────────────────────────────────────────────────────

    public void Add(LogLevel level, string category, string message)
    {
        var entry = new LogEntry(DateTime.UtcNow, level.ToString(), category, message);
        lock (_lock)
        {
            _ring[_head] = entry;
            _head        = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
        // Fire outside the lock so subscribers cannot deadlock on re-entry.
        OnEntry?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_lock) { Array.Clear(_ring, 0, Capacity); _head = 0; _count = 0; }
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns up to <paramref name="take"/> entries in chronological order,
    /// optionally filtered by minimum log level and/or category substring.
    /// </summary>
    public IReadOnlyList<LogEntry> GetLast(
        int     take     = 200,
        string? minLevel = null,
        string? category = null)
    {
        LogEntry?[] snapshot;
        int head, count;

        lock (_lock)
        {
            snapshot = (LogEntry?[])_ring.Clone();
            head     = _head;
            count    = _count;
        }

        var minSev = ParseLevel(minLevel);

        var result = new List<LogEntry>(Math.Min(take, count));
        for (int i = 0; i < count; i++)
        {
            var idx   = ((head - count + i) % Capacity + Capacity) % Capacity;
            var entry = snapshot[idx];
            if (entry is null) continue;
            if (minSev.HasValue && ParseLevel(entry.Level) < minSev) continue;
            if (category is not null && !entry.Category.Contains(category, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(entry);
        }

        // Return the last `take` after filtering.
        return result.Count > take ? result.GetRange(result.Count - take, take) : result;
    }

    // ── ILoggerProvider ────────────────────────────────────────────────────────

    public ILogger CreateLogger(string categoryName)
        => new LogBufferLogger(categoryName, this);

    public void Dispose() { }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static LogLevel? ParseLevel(string? s)
    {
        if (s is null) return null;
        if (s.Equals("Trace",       StringComparison.OrdinalIgnoreCase)) return LogLevel.Trace;
        if (s.Equals("Debug",       StringComparison.OrdinalIgnoreCase)) return LogLevel.Debug;
        if (s.Equals("Information", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("Info",        StringComparison.OrdinalIgnoreCase)) return LogLevel.Information;
        if (s.Equals("Warning",     StringComparison.OrdinalIgnoreCase)) return LogLevel.Warning;
        if (s.Equals("Error",       StringComparison.OrdinalIgnoreCase)) return LogLevel.Error;
        if (s.Equals("Critical",    StringComparison.OrdinalIgnoreCase)) return LogLevel.Critical;
        return null;
    }
}

// ── Logger implementation ──────────────────────────────────────────────────────

internal sealed class LogBufferLogger : ILogger
{
    private readonly string    _category;
    private readonly LogBuffer _buffer;

    public LogBufferLogger(string category, LogBuffer buffer)
    {
        _category = category;
        _buffer   = buffer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel                        level,
        EventId                         eventId,
        TState                          state,
        Exception?                      exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var msg = formatter(state, exception);
        if (exception is not null) msg += $"\n{exception.GetType().Name}: {exception.Message}";
        _buffer.Add(level, _category, msg);
    }
}

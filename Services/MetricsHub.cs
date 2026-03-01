using System.Diagnostics;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Thread-safe metrics store.
/// Telemetry/WS services call the Record* methods; admin endpoints read snapshots.
/// A 1-sample-per-second ring buffer enables the history endpoint.
/// </summary>
public sealed class MetricsHub : IDisposable
{
    private const int HistoryCap = 3600; // 1 h

    private readonly DateTime              _startUtc = DateTime.UtcNow;
    private readonly System.Threading.Timer _sampleTimer;

    // ── Counters (updated by multiple threads) ────────────────────────────────
    private int    _wsClients;
    private double _physicsHz;
    private double _graphicsHz;
    private long   _framesSent;
    private double _sumSendLatencyMs;
    private long   _droppedFrames;
    private int    _restartCount;

    // Snapshot helpers for frames/sec
    private long     _framesSentAtLastSample;
    private DateTime _lastSampleTime = DateTime.UtcNow;

    // ── Current snapshot properties ───────────────────────────────────────────
    public int    WsClientsConnected  => _wsClients;
    public double PhysicsHzActual     => _physicsHz;
    public double GraphicsHzActual    => _graphicsHz;
    public double AvgSendLatencyMs    => _framesSent > 0 ? _sumSendLatencyMs / _framesSent : 0;
    public long   DroppedFramesCount  => Interlocked.Read(ref _droppedFrames);
    public int    RestartCount        => _restartCount;
    public double FramesSentPerSec    { get; private set; }
    public DateTime LastFrameUtc      { get; private set; } = DateTime.MinValue;
    public double MemoryUsageMB       => Process.GetCurrentProcess().WorkingSet64 / 1_048_576.0;
    public double UptimeSeconds       => (DateTime.UtcNow - _startUtc).TotalSeconds;

    // ── History ring buffer ───────────────────────────────────────────────────
    private readonly MetricsSample[] _history = new MetricsSample[HistoryCap];
    private int  _histHead  = 0;
    private int  _histCount = 0;
    private readonly object _histLock = new();

    public MetricsHub()
    {
        _sampleTimer = new System.Threading.Timer(TakeSample, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    // ── Record methods (called by services) ───────────────────────────────────

    public void RecordPhysicsHz(double hz)  => Volatile.Write(ref _physicsHz,  hz);
    public void RecordGraphicsHz(double hz) => Volatile.Write(ref _graphicsHz, hz);
    public void RecordWsClients(int count)  => Interlocked.Exchange(ref _wsClients, count);
    public void RecordRestart()             => Interlocked.Increment(ref _restartCount);
    public void RecordFrameDropped()        => Interlocked.Increment(ref _droppedFrames);

    public void RecordFrameSent(double latencyMs)
    {
        Interlocked.Increment(ref _framesSent);
        LastFrameUtc = DateTime.UtcNow;
        // Use a lock for the double accumulator; double cannot be updated atomically
        // via Interlocked on all architectures.
        lock (this) { _sumSendLatencyMs += latencyMs; }
    }

    // ── History ───────────────────────────────────────────────────────────────

    public IReadOnlyList<MetricsSample> GetHistory(int seconds)
    {
        lock (_histLock)
        {
            var take = Math.Min(seconds, _histCount);
            var result = new MetricsSample[take];
            for (int i = 0; i < take; i++)
            {
                // Walk backward from head.
                var idx = ((_histHead - 1 - i) % HistoryCap + HistoryCap) % HistoryCap;
                result[take - 1 - i] = _history[idx];
            }
            return result;
        }
    }

    // ── Snapshot timer ────────────────────────────────────────────────────────

    private void TakeSample(object? _)
    {
        var now     = DateTime.UtcNow;
        var elapsed = (now - _lastSampleTime).TotalSeconds;
        var sent    = Interlocked.Read(ref _framesSent);

        if (elapsed > 0)
            FramesSentPerSec = (sent - _framesSentAtLastSample) / elapsed;

        _framesSentAtLastSample = sent;
        _lastSampleTime         = now;

        var sample = new MetricsSample(
            TimestampUtc:       now,
            WsClients:          _wsClients,
            PhysicsHz:          _physicsHz,
            GraphicsHz:         _graphicsHz,
            FramesSentPerSec:   FramesSentPerSec,
            AvgSendLatencyMs:   AvgSendLatencyMs,
            DroppedFrames:      Interlocked.Read(ref _droppedFrames),
            MemoryMB:           MemoryUsageMB);

        lock (_histLock)
        {
            _history[_histHead] = sample;
            _histHead = (_histHead + 1) % HistoryCap;
            if (_histCount < HistoryCap) _histCount++;
        }
    }

    public void Dispose() => _sampleTimer.Dispose();
}

public sealed record MetricsSample(
    DateTime TimestampUtc,
    int      WsClients,
    double   PhysicsHz,
    double   GraphicsHz,
    double   FramesSentPerSec,
    double   AvgSendLatencyMs,
    long     DroppedFrames,
    double   MemoryMB);

using AvoTelemetryAgent.Models;
using AvoTelemetryAgent.SharedMemory;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Reads Assetto Corsa shared memory and broadcasts TelemetryFrames.
/// No longer a BackgroundService; lifecycle is controlled by AgentRuntime.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private readonly AcSharedMemoryReader  _reader;
    private readonly WebSocketHub          _hub;
    private readonly MetricsHub            _metrics;
    private readonly ILogger<TelemetryService> _log;

    private long _seq;

    // Cached static state (survives start/stop cycles).
    private string          _lastCarModel  = string.Empty;
    private string          _lastTrack     = string.Empty;
    private SPageFileStatic _cachedStatic;
    private string          _cachedCarId   = string.Empty;
    private string          _cachedTrackId = string.Empty;
    private DateTime        _nextStaticRead = DateTime.MinValue;

    // Latest graphics (updated by graphics loop, read by physics loop).
    private SPageFileGraphics _latestGraphics;

    // Loop state.
    private CancellationTokenSource? _cts;
    private Task _loopTask = Task.CompletedTask;

    // Hz tracking (exponential moving average).
    private DateTime _lastPhysicsTick  = DateTime.UtcNow;
    private DateTime _lastGraphicsTick = DateTime.UtcNow;
    private double   _physicsHzEma;
    private double   _graphicsHzEma;

    // Hz EMA tuning: α=0.05 gives a smooth ~20-sample window.
    private const double EmaAlpha         = 0.05;
    private const double MaxTickElapsed   = 0.5; // sanity cap in seconds

    // Exposed for watchdog / runtime.
    public long     LastSeq        { get; private set; }
    public DateTime LastFrameUtc   { get; private set; } = DateTime.MinValue;
    public string   CarId          => _cachedCarId;
    public string   TrackId        => _cachedTrackId;

    public TelemetryService(
        AcSharedMemoryReader reader,
        WebSocketHub         hub,
        MetricsHub           metrics,
        ILogger<TelemetryService> log)
    {
        _reader  = reader;
        _hub     = hub;
        _metrics = metrics;
        _log     = log;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public Task StartAsync(AgentConfig config, CancellationToken appCt)
    {
        if (_cts is { IsCancellationRequested: false }) return Task.CompletedTask;

        _cts      = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        _loopTask = RunLoops(config, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try   { await _loopTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
    }

    public void Dispose() => _cts?.Dispose();

    // ── Loops ─────────────────────────────────────────────────────────────────

    private async Task RunLoops(AgentConfig config, CancellationToken ct)
    {
        var physicsInterval  = TimeSpan.FromSeconds(1.0 / Math.Max(1, config.PhysicsHz));
        var graphicsInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, config.GraphicsHz));
        var staticInterval   = TimeSpan.FromSeconds(1.0 / Math.Max(1, config.StaticHz));

        using var physicsTimer  = new PeriodicTimer(physicsInterval);
        using var graphicsTimer = new PeriodicTimer(graphicsInterval);

        var physicsTask  = RunPhysicsLoop(physicsTimer, ct);
        var graphicsTask = RunGraphicsLoop(graphicsTimer, staticInterval, ct);

        await Task.WhenAll(physicsTask, graphicsTask).ConfigureAwait(false);
    }

    private async Task RunPhysicsLoop(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                _reader.CheckConnected();
                var physics = _reader.ReadPhysics();
                var frame   = BuildFrame(physics, _latestGraphics, _reader.IsConnected);

                var t0 = DateTime.UtcNow;
                await _hub.BroadcastAsync(frame, ct).ConfigureAwait(false);
                _metrics.RecordFrameSent((DateTime.UtcNow - t0).TotalMilliseconds);

                LastSeq      = frame.Seq;
                LastFrameUtc = DateTime.UtcNow;

                // Hz EMA (α = 0.05 for smooth display).
                var now     = DateTime.UtcNow;
                var elapsed = (now - _lastPhysicsTick).TotalSeconds;
                _lastPhysicsTick = now;
                if (elapsed is > 0 and < MaxTickElapsed)
                    _physicsHzEma = _physicsHzEma * (1 - EmaAlpha) + (1.0 / elapsed) * EmaAlpha;

                _metrics.RecordPhysicsHz(_physicsHzEma);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Error in physics loop");
            }
        }
    }

    private async Task RunGraphicsLoop(
        PeriodicTimer timer, TimeSpan staticInterval, CancellationToken ct)
    {
        var nextStatic = DateTime.MinValue;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                _latestGraphics = _reader.ReadGraphics();

                var now = DateTime.UtcNow;
                if (now >= nextStatic)
                {
                    MaybeRefreshStatic();
                    nextStatic = now.Add(staticInterval);
                }

                // Hz EMA.
                var elapsed = (now - _lastGraphicsTick).TotalSeconds;
                _lastGraphicsTick = now;
                if (elapsed is > 0 and < MaxTickElapsed)
                    _graphicsHzEma = _graphicsHzEma * (1 - EmaAlpha) + (1.0 / elapsed) * EmaAlpha;

                _metrics.RecordGraphicsHz(_graphicsHzEma);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Error in graphics loop");
            }
        }
    }

    // ── Static refresh ────────────────────────────────────────────────────────

    private unsafe void MaybeRefreshStatic()
    {
        _cachedStatic   = _reader.ReadStatic();
        _nextStaticRead = DateTime.UtcNow;

        string carModel, track;
        fixed (SPageFileStatic* s = &_cachedStatic)
        {
            carModel = AcSharedMemoryReader.ReadWString(s->CarModel, 33);
            track    = AcSharedMemoryReader.ReadWString(s->Track, 33);
        }

        if (carModel != _lastCarModel || track != _lastTrack)
        {
            _lastCarModel  = carModel;
            _lastTrack     = track;
            _cachedCarId   = carModel;
            _cachedTrackId = track;
            _log.LogInformation("Static changed: car={Car} track={Track}", carModel, track);
        }
    }

    // ── Frame builder ─────────────────────────────────────────────────────────

    private unsafe TelemetryFrame BuildFrame(
        SPageFilePhysics  phy,
        SPageFileGraphics gfx,
        bool connected)
    {
        var seq = Interlocked.Increment(ref _seq);

        SPageFilePhysics* p = &phy;
        float latG         = p->AccG[0];
        float longG        = p->AccG[2];
        float yawRate      = p->LocalAngularVel[1];
        float[] wheelSlip    = [p->WheelSlip[0],       p->WheelSlip[1],       p->WheelSlip[2],       p->WheelSlip[3]];
        float[] tyreTemp     = [p->TyreTempI[0],        p->TyreTempI[1],        p->TyreTempI[2],        p->TyreTempI[3]];
        float[] tyrePressure = [p->WheelsPressure[0],   p->WheelsPressure[1],   p->WheelsPressure[2],   p->WheelsPressure[3]];

        return new TelemetryFrame
        {
            TUtc    = DateTime.UtcNow.ToString("O"),
            Seq     = seq,
            CarId   = _cachedCarId,
            TrackId = _cachedTrackId,

            Physics = new PhysicsDto
            {
                SpeedKmh     = phy.SpeedKmh,
                Rpm          = phy.Rpms,
                Gear         = phy.Gear,
                Throttle     = phy.Gas,
                Brake        = phy.Brake,
                Steer        = phy.SteerAngle,
                Clutch       = phy.Clutch,
                LatG         = latG,
                LongG        = longG,
                YawRate      = yawRate,
                WheelSlip    = wheelSlip,
                TyreTemp     = tyreTemp,
                TyrePressure = tyrePressure,
            },

            Graphics = new GraphicsDto
            {
                Session       = gfx.Session.ToString(),
                AcStatus      = gfx.Status.ToString(),
                Lap           = gfx.CompletedLaps + 1,
                LapTimeMs     = gfx.ICurrentTime,
                BestLapMs     = gfx.IBestTime,
                Position      = gfx.Position,
                CompletedLaps = gfx.CompletedLaps,
                CurrentTimeMs = gfx.ICurrentTime,
            },

            Statics = new StaticsDto
            {
                CarModel = _cachedCarId,
                Track    = _cachedTrackId,
                MaxRpm   = _cachedStatic.MaxRpm,
                MaxFuel  = _cachedStatic.MaxFuel,
            },

            Status = new StatusDto
            {
                Connected = connected,
                Message   = connected ? "ok" : "AC not running",
            },
        };
    }
}


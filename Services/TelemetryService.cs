using AvoTelemetryAgent.Models;
using AvoTelemetryAgent.SharedMemory;
using Microsoft.Extensions.Options;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Background service that reads Assetto Corsa shared memory at configured rates
/// and publishes TelemetryFrames to the WebSocketHub.
///
/// - Physics loop : PhysicsHz  (default 60 Hz)
/// - Graphics loop: GraphicsHz (default 20 Hz)
/// - Static read  : every StaticIntervalMs AND whenever carModel/track changes
/// </summary>
public sealed class TelemetryService : BackgroundService
{
    private readonly AcSharedMemoryReader _reader;
    private readonly WebSocketHub         _hub;
    private readonly AgentOptions         _opts;
    private readonly ILogger<TelemetryService> _log;

    private long _seq;

    // Last-known static values used to detect changes.
    private string _lastCarModel = string.Empty;
    private string _lastTrack    = string.Empty;

    // Cached static data (re-read on change or timer).
    private SPageFileStatic   _cachedStatic;
    private string            _cachedCarId  = string.Empty;
    private string            _cachedTrackId = string.Empty;
    private DateTime          _nextStaticRead = DateTime.MinValue;

    public TelemetryService(
        AcSharedMemoryReader reader,
        WebSocketHub hub,
        IOptions<AgentOptions> opts,
        ILogger<TelemetryService> log)
    {
        _reader = reader;
        _hub    = hub;
        _opts   = opts.Value;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var physicsInterval  = TimeSpan.FromSeconds(1.0 / _opts.PhysicsHz);
        var graphicsInterval = TimeSpan.FromSeconds(1.0 / _opts.GraphicsHz);

        var physicsTimer  = new PeriodicTimer(physicsInterval);
        var graphicsTimer = new PeriodicTimer(graphicsInterval);

        // Run physics and graphics loops concurrently.
        var physicsTask  = RunPhysicsLoop(physicsTimer,  ct);
        var graphicsTask = RunGraphicsLoop(graphicsTimer, ct);

        await Task.WhenAll(physicsTask, graphicsTask).ConfigureAwait(false);
    }

    // ── Physics loop ──────────────────────────────────────────────────────────

    private async Task RunPhysicsLoop(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                bool connected = _reader.CheckConnected();
                MaybeRefreshStatic();

                var physics  = _reader.ReadPhysics();
                var graphics = _reader.ReadGraphics();

                var frame = BuildFrame(physics, graphics, connected);
                await _hub.BroadcastAsync(frame, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Error in physics loop");
            }
        }
    }

    // ── Graphics loop (lower rate – updates graphics sub-object only) ─────────
    // We keep a separate loop so graphics fields update at 20 Hz
    // while physics refresh at 60 Hz.  Both publish full frames.

    private async Task RunGraphicsLoop(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            // Graphics loop does nothing extra here; the physics loop already
            // embeds the latest graphics read.  This stub is left for future use
            // (e.g. graphics-only sub-frame broadcasts).
            await Task.Yield();
        }
    }

    // ── Static refresh ────────────────────────────────────────────────────────

    private unsafe void MaybeRefreshStatic()
    {
        var now = DateTime.UtcNow;
        if (now < _nextStaticRead && _cachedCarId.Length > 0)
            return;

        _cachedStatic  = _reader.ReadStatic();
        _nextStaticRead = now.AddMilliseconds(_opts.StaticIntervalMs);

        // _cachedStatic is a heap field; pin it before accessing fixed buffers.
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

        // phy is a local stack variable — already fixed; take address directly.
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

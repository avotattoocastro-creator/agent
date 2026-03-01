using AvoTelemetryAgent.SharedMemory;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Central runtime controller.  Owns the streaming lifecycle and exposes
/// observable state used by the dashboard and watchdog.
///
/// Naming: StartStreamingAsync / StopStreamingAsync are used to avoid
/// collision with IHostedService.StartAsync / StopAsync.
/// </summary>
public sealed class AgentRuntime : BackgroundService
{
    private readonly TelemetryService    _telemetry;
    private readonly WebSocketHub        _hub;
    private readonly AcSharedMemoryReader _reader;
    private readonly AgentConfigService  _configService;
    private readonly MetricsHub          _metrics;
    private readonly ILogger<AgentRuntime> _log;

    private CancellationToken _appStopping;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Observable state ──────────────────────────────────────────────────────
    public bool     IsRunning           { get; private set; }
    public DateTime StartedUtc          { get; private set; }
    public string   LastStatusMessage   { get; private set; } = "initializing";
    public string?  LastError           { get; private set; }
    public bool     AcConnected         => _reader.IsConnected;
    public string   CarId               => _telemetry.CarId;
    public string   TrackId             => _telemetry.TrackId;
    public long     LastSeq             => _telemetry.LastSeq;
    public DateTime LastFrameUtc        => _telemetry.LastFrameUtc;
    public int      ConnectedClients    => _hub.ClientCount;
    public double   PhysicsHzActual     => _metrics.PhysicsHzActual;
    public double   GraphicsHzActual    => _metrics.GraphicsHzActual;

    public AgentRuntime(
        TelemetryService     telemetry,
        WebSocketHub         hub,
        AcSharedMemoryReader reader,
        AgentConfigService   configService,
        MetricsHub           metrics,
        ILogger<AgentRuntime> log)
    {
        _telemetry     = telemetry;
        _hub           = hub;
        _reader        = reader;
        _configService = configService;
        _metrics       = metrics;
        _log           = log;

        // Wire heartbeat status so WebSocketHub reflects agent state
        // without a circular DI dependency.
        _hub.GetCurrentStatus = () => new Models.StatusDto
        {
            Connected = IsRunning && AcConnected,
            Message   = IsRunning ? (AcConnected ? "ok" : "AC not running") : "Agent stopped",
        };
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _appStopping = ct;

        if (_configService.Current.Agent.AutoStartStreaming)
            await StartStreamingAsync().ConfigureAwait(false);

        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    // ── Streaming control ─────────────────────────────────────────────────────

    public async Task StartStreamingAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            var config = _configService.Current;
            await _telemetry.StartAsync(config, _appStopping).ConfigureAwait(false);
            IsRunning         = true;
            StartedUtc        = DateTime.UtcNow;
            LastStatusMessage = "running";
            LastError         = null;
            _log.LogInformation("Agent streaming started");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogError(ex, "Agent streaming start failed");
            throw;
        }
        finally { _lock.Release(); }
    }

    public async Task StopStreamingAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning) return;
            await _telemetry.StopAsync().ConfigureAwait(false);
            IsRunning         = false;
            LastStatusMessage = "stopped";
            _log.LogInformation("Agent streaming stopped");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogError(ex, "Agent streaming stop failed");
            throw;
        }
        finally { _lock.Release(); }
    }
}

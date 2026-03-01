namespace AvoTelemetryAgent.Services;

/// <summary>
/// Polls every 2 s; if the telemetry seq counter stops advancing for more
/// than 3 s while the agent is running, it restarts the streaming loops.
/// </summary>
public sealed class WatchdogService : BackgroundService
{
    private readonly AgentRuntime            _runtime;
    private readonly MetricsHub              _metrics;
    private readonly ILogger<WatchdogService> _log;

    // Stall detection: restart if the seq counter hasn't advanced for this many seconds.
    private const double StallThresholdSeconds = 3.0;

    public WatchdogService(
        AgentRuntime            runtime,
        MetricsHub              metrics,
        ILogger<WatchdogService> log)
    {
        _runtime = runtime;
        _metrics = metrics;
        _log     = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        long     lastSeq       = 0;
        DateTime lastSeqChange = DateTime.UtcNow;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (!_runtime.IsRunning)
            {
                // Reset baseline so watchdog doesn't fire immediately on start.
                lastSeq       = _runtime.LastSeq;
                lastSeqChange = DateTime.UtcNow;
                continue;
            }

            var seq = _runtime.LastSeq;
            if (seq != lastSeq)
            {
                lastSeq       = seq;
                lastSeqChange = DateTime.UtcNow;
                continue;
            }

            if ((DateTime.UtcNow - lastSeqChange).TotalSeconds > StallThresholdSeconds)
            {
                _log.LogWarning(
                    "Watchdog: telemetry stalled (seq={Seq}), restarting loops", seq);

                await _runtime.StopStreamingAsync().ConfigureAwait(false);
                await _runtime.StartStreamingAsync().ConfigureAwait(false);
                _metrics.RecordRestart();

                lastSeq       = 0;
                lastSeqChange = DateTime.UtcNow;
            }
        }
    }
}

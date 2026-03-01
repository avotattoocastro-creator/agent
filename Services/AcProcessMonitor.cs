using System.Diagnostics;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Polls for the "acs" process every second.
/// Auto-starts streaming when AC appears (3-tick debounce);
/// auto-stops when AC disappears if AutoStopWhenAcCloses is set.
/// </summary>
public sealed class AcProcessMonitor : BackgroundService
{
    private readonly AgentRuntime               _runtime;
    private readonly AgentConfigService         _configService;
    private readonly ILogger<AcProcessMonitor>   _log;

    // Debounce: process must be present/absent for ConfirmationTicks consecutive polls.
    private const int MaxStreak          = 4;
    private const int ConfirmationTicks  = 3;
    private int  _presentStreak = 0;
    private int  _absentStreak  = 0;
    private bool _wasRunning    = false;

    // Publicly readable so the dashboard can show AC process state.
    public bool AcProcessRunning { get; private set; }

    public AcProcessMonitor(
        AgentRuntime             runtime,
        AgentConfigService       configService,
        ILogger<AcProcessMonitor> log)
    {
        _runtime       = runtime;
        _configService = configService;
        _log           = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            bool present;
            try { present = Process.GetProcessesByName("acs").Length > 0; }
            catch { present = false; }

            if (present) { _presentStreak = Math.Min(_presentStreak + 1, MaxStreak); _absentStreak  = 0; }
            else         { _absentStreak  = Math.Min(_absentStreak  + 1, MaxStreak); _presentStreak = 0; }

            bool confirmed    = _presentStreak >= ConfirmationTicks;
            bool confirmedOff = _absentStreak  >= ConfirmationTicks;

            AcProcessRunning = confirmed || (!confirmedOff && _wasRunning);

            var agent = _configService.Current.Agent;

            if (confirmed && !_wasRunning)
            {
                _wasRunning = true;
                _log.LogInformation("Assetto Corsa detected");
                if (agent.AutoStartStreaming && !_runtime.IsRunning)
                    await _runtime.StartStreamingAsync().ConfigureAwait(false);
            }
            else if (confirmedOff && _wasRunning)
            {
                _wasRunning = false;
                _log.LogInformation("Assetto Corsa closed");
                if (agent.AutoStopWhenAcCloses && _runtime.IsRunning)
                    await _runtime.StopStreamingAsync().ConfigureAwait(false);
            }
        }
    }
}

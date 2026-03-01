using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Broadcasts a UDP discovery beacon every <see cref="DiscoverySection.IntervalMs"/> ms.
/// Reads config on each tick so Discovery.Enabled / Port changes take effect immediately.
/// Payload: AVO_AGENT|{httpPort}|{machineName}
/// </summary>
public sealed class LanDiscoveryService : BackgroundService
{
    private readonly AgentConfigService          _configService;
    private readonly ILogger<LanDiscoveryService> _log;

    public LanDiscoveryService(
        AgentConfigService configService,
        ILogger<LanDiscoveryService> log)
    {
        _configService = configService;
        _log           = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };

        _log.LogInformation("LAN discovery service started");

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        var nextSend = DateTime.MinValue;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var disc = _configService.Current.Discovery;
            if (!disc.Enabled) continue;

            if (DateTime.UtcNow < nextSend) continue;
            nextSend = DateTime.UtcNow.AddMilliseconds(disc.IntervalMs);

            try
            {
                var port     = _configService.Current.Port;
                var payload  = Encoding.UTF8.GetBytes($"AVO_AGENT|{port}|{Environment.MachineName}");
                var endpoint = new IPEndPoint(IPAddress.Broadcast, disc.Port);
                await udp.SendAsync(payload, payload.Length, endpoint).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "UDP broadcast failed");
            }
        }
    }
}

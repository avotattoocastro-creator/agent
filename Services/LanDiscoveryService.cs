using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Broadcasts a UDP discovery beacon every 2 seconds on the configured port.
/// Payload format:  AVO_AGENT|{httpPort}|{machineName}
/// </summary>
public sealed class LanDiscoveryService : BackgroundService
{
    private readonly AgentOptions _opts;
    private readonly ILogger<LanDiscoveryService> _log;

    public LanDiscoveryService(
        IOptions<AgentOptions> opts,
        ILogger<LanDiscoveryService> log)
    {
        _opts = opts.Value;
        _log  = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };

        var payload  = Encoding.UTF8.GetBytes(
            $"AVO_AGENT|{_opts.HttpPort}|{Environment.MachineName}");
        var endpoint = new IPEndPoint(IPAddress.Broadcast, _opts.DiscoveryPort);

        _log.LogInformation(
            "LAN discovery: broadcasting on UDP port {Port} every 2 s", _opts.DiscoveryPort);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await udp.SendAsync(payload, payload.Length, endpoint)
                         .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "UDP broadcast failed");
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AvoTelemetryAgent.Models;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Manages all connected WebSocket clients.
///
/// Each client gets a bounded channel (capacity 1, DropOldest) so slow clients
/// never block the broadcast path and always receive the freshest frame.
///
/// A heartbeat fires every second; when the agent is stopped the delegate
/// GetCurrentStatus supplies the appropriate status message without a circular
/// dependency on AgentRuntime.
/// </summary>
public sealed class WebSocketHub : IDisposable
{
    private record ClientEntry(
        System.Threading.Channels.Channel<TelemetryFrame> Channel,
        WebSocket Socket);

    private readonly ConcurrentDictionary<Guid, ClientEntry> _clients = new();
    private readonly MetricsHub _metrics;
    private readonly System.Threading.Timer _heartbeat;
    private volatile TelemetryFrame? _lastFrame;
    private long _seq;

    /// <summary>
    /// Set by AgentRuntime after construction; drives the heartbeat status
    /// without creating a circular DI dependency.
    /// </summary>
    public Func<StatusDto>? GetCurrentStatus { get; set; }

    public int ClientCount => _clients.Count;

    public WebSocketHub(MetricsHub metrics)
    {
        _metrics   = metrics;
        _heartbeat = new System.Threading.Timer(OnHeartbeat, null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    // ── Registration ──────────────────────────────────────────────────────────

    public async Task HandleClientAsync(WebSocket ws, CancellationToken ct)
    {
        var id      = Guid.NewGuid();
        var channel = System.Threading.Channels.Channel.CreateBounded<TelemetryFrame>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

        _clients[id] = new ClientEntry(channel, ws);
        _metrics.RecordWsClients(_clients.Count);
        try
        {
            await SendLoop(ws, channel, _metrics, ct).ConfigureAwait(false);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _metrics.RecordWsClients(_clients.Count);
        }
    }

    // ── Broadcast ─────────────────────────────────────────────────────────────

    public async Task BroadcastAsync(TelemetryFrame frame, CancellationToken ct)
    {
        _lastFrame = frame;
        foreach (var (_, entry) in _clients)
        {
            // With DropOldest and capacity 1: if the channel already has an
            // unconsumed item it will be dropped (client is slow).
            if (entry.Channel.Reader.Count >= 1) _metrics.RecordFrameDropped();
            entry.Channel.Writer.TryWrite(frame);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    private void OnHeartbeat(object? _)
    {
        var last   = _lastFrame;
        var status = GetCurrentStatus?.Invoke()
                  ?? last?.Status
                  ?? new StatusDto { Connected = false, Message = "heartbeat" };

        var heartbeat = new TelemetryFrame
        {
            TUtc     = DateTime.UtcNow.ToString("O"),
            Seq      = Interlocked.Increment(ref _seq),
            CarId    = last?.CarId    ?? string.Empty,
            TrackId  = last?.TrackId  ?? string.Empty,
            Physics  = last?.Physics  ?? new PhysicsDto(),
            Graphics = last?.Graphics ?? new GraphicsDto(),
            Statics  = last?.Statics  ?? new StaticsDto(),
            Status   = status,
        };

        foreach (var (_, entry) in _clients)
            entry.Channel.Writer.TryWrite(heartbeat);
    }

    // ── Per-client send loop ──────────────────────────────────────────────────

    private static async Task SendLoop(
        WebSocket ws,
        System.Threading.Channels.Channel<TelemetryFrame> channel,
        MetricsHub metrics,
        CancellationToken ct)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (ws.State != WebSocketState.Open) break;

            var t0    = DateTime.UtcNow;
            var json  = JsonSerializer.Serialize(frame, opts);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);

            metrics.RecordFrameSent((DateTime.UtcNow - t0).TotalMilliseconds);
        }
    }

    public void Dispose() => _heartbeat.Dispose();
}

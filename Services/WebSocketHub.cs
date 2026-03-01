using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AvoTelemetryAgent.Models;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Manages all connected WebSocket clients.
///
/// Each client gets a bounded channel with capacity 1 and DropOldest policy:
/// if the client is slow, stale frames are silently dropped and only the
/// latest frame is delivered.
///
/// A heartbeat timer fires every second to push a status-only frame to every
/// client even when Assetto Corsa is not running.
/// </summary>
public sealed class WebSocketHub : IDisposable
{
    private record ClientEntry(
        System.Threading.Channels.Channel<TelemetryFrame> Channel,
        WebSocket Socket);

    private readonly ConcurrentDictionary<Guid, ClientEntry> _clients = new();
    private readonly Timer _heartbeat;
    private volatile TelemetryFrame? _lastFrame;
    private long _seq;

    public int ClientCount => _clients.Count;

    public WebSocketHub()
    {
        _heartbeat = new Timer(OnHeartbeat, null,
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
        try
        {
            await SendLoop(ws, channel, ct).ConfigureAwait(false);
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    // ── Broadcast ─────────────────────────────────────────────────────────────

    public async Task BroadcastAsync(TelemetryFrame frame, CancellationToken ct)
    {
        _lastFrame = frame;
        foreach (var (_, entry) in _clients)
            entry.Channel.Writer.TryWrite(frame);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    private void OnHeartbeat(object? _)
    {
        var last = _lastFrame;
        // Always create a new frame so we don't mutate a shared reference.
        var heartbeat = new TelemetryFrame
        {
            TUtc     = DateTime.UtcNow.ToString("O"),
            Seq      = Interlocked.Increment(ref _seq),
            CarId    = last?.CarId    ?? string.Empty,
            TrackId  = last?.TrackId  ?? string.Empty,
            Physics  = last?.Physics  ?? new PhysicsDto(),
            Graphics = last?.Graphics ?? new GraphicsDto(),
            Statics  = last?.Statics  ?? new StaticsDto(),
            Status   = last?.Status   ?? new StatusDto { Connected = false, Message = "heartbeat" },
        };

        foreach (var (_, entry) in _clients)
            entry.Channel.Writer.TryWrite(heartbeat);
    }

    // ── Per-client send loop ──────────────────────────────────────────────────

    private static async Task SendLoop(
        WebSocket ws,
        System.Threading.Channels.Channel<TelemetryFrame> channel,
        CancellationToken ct)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (ws.State != WebSocketState.Open) break;

            var json  = JsonSerializer.Serialize(frame, opts);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _heartbeat.Dispose();
}

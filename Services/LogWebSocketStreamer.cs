using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Streams log entries over a WebSocket connection.
///
/// On connect: sends the last <c>tail</c> buffered entries.
/// While connected: forwards every new entry via <see cref="LogBuffer.OnEntry"/>.
/// On disconnect: unsubscribes cleanly.
/// </summary>
public sealed class LogWebSocketStreamer
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly LogBuffer _buffer;

    public LogWebSocketStreamer(LogBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Handles a single WebSocket client for live log streaming.
    /// </summary>
    /// <param name="ws">Accepted WebSocket.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <param name="tail">Number of historical entries to replay on connect (0–2000).</param>
    public async Task HandleAsync(WebSocket ws, CancellationToken ct, int tail = 200)
    {
        // Bounded channel: capacity 256, drop oldest so slow clients never
        // block the logging hot-path.
        var channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(256)
            {
                FullMode            = BoundedChannelFullMode.DropOldest,
                SingleReader        = true,
                SingleWriter        = false,
                AllowSynchronousContinuations = false,
            });

        void OnNewEntry(LogEntry e) => channel.Writer.TryWrite(e);
        _buffer.OnEntry += OnNewEntry;
        try
        {
            // ── Send historical backlog ────────────────────────────────────────
            foreach (var entry in _buffer.GetLast(tail))
            {
                if (ws.State != WebSocketState.Open) return;
                await SendEntryAsync(ws, entry, ct).ConfigureAwait(false);
            }

            // ── Stream live entries until the client disconnects ───────────────
            // Also drain any entries that arrived between GetLast() and subscribe.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Background task: read WebSocket frames to detect client close.
            var readTask = ReadUntilCloseAsync(ws, cts);

            await foreach (var entry in channel.Reader.ReadAllAsync(cts.Token)
                               .ConfigureAwait(false))
            {
                if (ws.State != WebSocketState.Open) break;
                await SendEntryAsync(ws, entry, cts.Token).ConfigureAwait(false);
            }

            await readTask.ConfigureAwait(false);
        }
        finally
        {
            _buffer.OnEntry -= OnNewEntry;
            channel.Writer.TryComplete();
        }
    }

    private static async Task ReadUntilCloseAsync(
        WebSocket ws,
        CancellationTokenSource cts)
    {
        var buf = new byte[64];
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(
                    new ArraySegment<byte>(buf), cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (WebSocketException)         { /* client disconnected abruptly */ }
        finally { cts.Cancel(); }
    }

    private static async Task SendEntryAsync(WebSocket ws, LogEntry e, CancellationToken ct)
    {
        var payload = new
        {
            tUtc = e.TimestampUtc.ToString("O"),
            lvl  = e.Level,
            cat  = e.Category,
            msg  = e.Message,
        };
        var json  = JsonSerializer.Serialize(payload, _jsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct).ConfigureAwait(false);
    }
}

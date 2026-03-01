using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using AvoTelemetryAgent.Services;
using AvoTelemetryAgent.SharedMemory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────
builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection("AvoAgent"));

// ── Services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<AcSharedMemoryReader>();
builder.Services.AddSingleton<WebSocketHub>();
builder.Services.AddHostedService<TelemetryService>();
builder.Services.AddHostedService<LanDiscoveryService>();

// ── Kestrel port ──────────────────────────────────────────────────────────
var agentOpts = builder.Configuration
    .GetSection("AvoAgent")
    .Get<AgentOptions>() ?? new AgentOptions();

builder.WebHost.UseUrls($"http://0.0.0.0:{agentOpts.HttpPort}");

var app = builder.Build();

// ── WebSocket middleware ───────────────────────────────────────────────────
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// ── /ws endpoint ─────────────────────────────────────────────────────────
app.Map("/ws", async (HttpContext context, WebSocketHub hub, IOptions<AgentOptions> opts) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket upgrade required.");
        return;
    }

    // Auth: token via query string OR header
    var expected = opts.Value.Token;
    var token    = context.Request.Query["token"].FirstOrDefault()
                ?? context.Request.Headers["X-AVO-TOKEN"].FirstOrDefault();

    if (string.IsNullOrWhiteSpace(token) || token != expected)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Unauthorized.");
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    await hub.HandleClientAsync(ws, context.RequestAborted);
});

// ── GET /api/ping ──────────────────────────────────────────────────────────
app.MapGet("/api/ping", () => Results.Ok(new
{
    ok      = true,
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
    timeUtc = DateTime.UtcNow,
}));

// ── GET /api/info ──────────────────────────────────────────────────────────
app.MapGet("/api/info", (
    WebSocketHub hub,
    AcSharedMemoryReader reader,
    IOptions<AgentOptions> opts) =>
{
    reader.CheckConnected();
    return Results.Ok(new
    {
        machine      = Environment.MachineName,
        agentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
        connected    = reader.IsConnected,
        carId        = (string?)null,   // populated via telemetry service state
        trackId      = (string?)null,
        clients      = hub.ClientCount,
    });
});

// ── POST /api/setup/apply ─────────────────────────────────────────────────
app.MapPost("/api/setup/apply", async (
    HttpContext context,
    IOptions<AgentOptions> opts,
    [FromBody] SetupApplyRequest req) =>
{
    // Auth
    var expected = opts.Value.Token;
    var token    = context.Request.Headers["X-AVO-TOKEN"].FirstOrDefault()
                ?? context.Request.Query["token"].FirstOrDefault();

    if (string.IsNullOrWhiteSpace(token) || token != expected)
        return Results.Unauthorized();

    // Validate required fields
    if (string.IsNullOrWhiteSpace(req.CarId)     ||
        string.IsNullOrWhiteSpace(req.TrackId)   ||
        string.IsNullOrWhiteSpace(req.FileName)  ||
        string.IsNullOrWhiteSpace(req.SetupText))
    {
        return Results.BadRequest(new { error = "carId, trackId, fileName and setupText are required." });
    }

    // Resolve base directory
    var docs     = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    var baseDir  = Path.Combine(docs, "Assetto Corsa", "setups");

    string savedPath;

    if (!string.IsNullOrWhiteSpace(req.RelativePathOptional))
    {
        // Use relative path but prevent directory traversal.
        var relative = Path.GetFullPath(
            Path.Combine(baseDir, req.RelativePathOptional));

        if (!relative.StartsWith(
                Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "Invalid relative path." });
        }

        savedPath = relative;
    }
    else
    {
        // Sanitise each segment to prevent traversal via carId/trackId/fileName.
        var safeCarId   = SanitisePathSegment(req.CarId);
        var safeTrackId = SanitisePathSegment(req.TrackId);
        var safeFile    = SanitisePathSegment(req.FileName);

        if (safeCarId is null || safeTrackId is null || safeFile is null)
            return Results.BadRequest(new { error = "Path segment contains invalid characters." });

        var dir = Path.Combine(baseDir, safeCarId, safeTrackId);
        savedPath = Path.Combine(dir, safeFile.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
            ? safeFile
            : safeFile + ".ini");

        // Double-check the resolved path is still inside baseDir.
        if (!Path.GetFullPath(savedPath).StartsWith(
                Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "Resolved path escapes the setup directory." });
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(savedPath)!);
    await File.WriteAllTextAsync(savedPath, req.SetupText);

    return Results.Ok(new { ok = true, savedPath });
});

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────

static string? SanitisePathSegment(string segment)
{
    // Reject empty, dot-only, or segments with path separators / reserved names.
    if (string.IsNullOrWhiteSpace(segment)) return null;
    if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
    if (segment is "." or "..") return null;
    return segment;
}

// ── Request body ──────────────────────────────────────────────────────────
record SetupApplyRequest(
    string? CarId,
    string? TrackId,
    string? FileName,
    string? SetupText,
    string? RelativePathOptional);

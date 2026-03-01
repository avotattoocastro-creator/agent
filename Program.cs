using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AvoTelemetryAgent.Services;
using AvoTelemetryAgent.SharedMemory;
using Microsoft.AspNetCore.Mvc;

// ── Log buffer (created before DI so the provider can be registered) ─────────
var logBuffer = new LogBuffer();

var builder = WebApplication.CreateBuilder(args);

// ── Command-line overrides ─────────────────────────────────────────────────
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port"         when i + 1 < args.Length: builder.Configuration["AvoAgent:Port"]                  = args[++i]; break;
        case "--token"        when i + 1 < args.Length: builder.Configuration["AvoAgent:Token"]                 = args[++i]; break;
        case "--no-discovery":                           builder.Configuration["AvoAgent:Discovery:Enabled"]     = "false";   break;
        case "--admin-remote":                           builder.Configuration["AvoAgent:AdminUi:AllowRemote"]   = "true";    break;
    }
}

// ── Logging ───────────────────────────────────────────────────────────────
builder.Logging.AddProvider(logBuffer);

// ── Services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton(logBuffer);
var configSvc = new AgentConfigService(builder.Configuration);
builder.Services.AddSingleton(configSvc);
builder.Services.AddSingleton<MetricsHub>();
builder.Services.AddSingleton<AcSharedMemoryReader>();
builder.Services.AddSingleton<WebSocketHub>();
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<WindowsAutostartService>();
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddSingleton<AcProcessMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentRuntime>());
builder.Services.AddHostedService<WatchdogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcProcessMonitor>());
builder.Services.AddHostedService<LanDiscoveryService>();

// ── Kestrel ───────────────────────────────────────────────────────────────
builder.WebHost.UseUrls($"http://0.0.0.0:{configSvc.Current.Port}");

var app = builder.Build();

// ── Static files (wwwroot → dashboard) ───────────────────────────────────
app.UseStaticFiles();

// ── WebSocket middleware ───────────────────────────────────────────────────
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// ── Localhost guard for admin paths ───────────────────────────────────────
app.Use(async (ctx, next) =>
{
    if (IsAdminPath(ctx.Request.Path))
    {
        var cfg = ctx.RequestServices.GetRequiredService<AgentConfigService>().Current;
        if (cfg.AdminUi.BindLocalhostOnly && !cfg.AdminUi.AllowRemote)
        {
            if (!IsLocal(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Admin UI is restricted to localhost.");
                return;
            }
        }
    }
    await next();
});

// ── /ws ──────────────────────────────────────────────────────────────────
app.Map("/ws", async (HttpContext ctx, WebSocketHub hub, AgentConfigService cfgSvc) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("WebSocket upgrade required.");
        return;
    }
    if (!TokenOk(ctx, cfgSvc))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("Unauthorized.");
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleClientAsync(ws, ctx.RequestAborted);
});

// ── GET /api/ping ─────────────────────────────────────────────────────────
app.MapGet("/api/ping", () => Results.Ok(new
{
    ok      = true,
    version = AgentVersion(),
    timeUtc = DateTime.UtcNow,
}));

// ── GET /api/info ─────────────────────────────────────────────────────────
app.MapGet("/api/info", (WebSocketHub hub, AcSharedMemoryReader reader) =>
{
    reader.CheckConnected();
    return Results.Ok(new
    {
        machine      = Environment.MachineName,
        agentVersion = AgentVersion(),
        connected    = reader.IsConnected,
        clients      = hub.ClientCount,
    });
});

// ── POST /api/setup/apply ─────────────────────────────────────────────────
app.MapPost("/api/setup/apply", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromBody] SetupApplyRequest req) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.CarId)    ||
        string.IsNullOrWhiteSpace(req.TrackId)  ||
        string.IsNullOrWhiteSpace(req.FileName) ||
        string.IsNullOrWhiteSpace(req.SetupText))
        return Results.BadRequest(new { error = "carId, trackId, fileName and setupText are required." });

    // Only .ini files allowed.
    var safeFileName = SanitiseSegment(req.FileName);
    if (safeFileName is null)
        return Results.BadRequest(new { error = "Invalid fileName." });
    if (!safeFileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        safeFileName += ".ini";

    var root = cfgSvc.Current.Setup.DefaultRoot;
    if (string.IsNullOrWhiteSpace(root))
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        root = Path.Combine(docs, "Assetto Corsa", "setups");
    }

    string savedPath;
    if (!string.IsNullOrWhiteSpace(req.RelativePathOptional))
    {
        var resolved = Path.GetFullPath(Path.Combine(root, req.RelativePathOptional));
        if (!resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid relative path." });
        savedPath = resolved;
    }
    else
    {
        var safeCarId   = SanitiseSegment(req.CarId);
        var safeTrackId = SanitiseSegment(req.TrackId);
        if (safeCarId is null || safeTrackId is null)
            return Results.BadRequest(new { error = "Invalid carId or trackId." });
        var dir = Path.Combine(root, safeCarId, safeTrackId);
        savedPath = Path.Combine(dir, safeFileName);
        if (!Path.GetFullPath(savedPath).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Resolved path escapes setup directory." });
    }

    Directory.CreateDirectory(Path.GetDirectoryName(savedPath)!);
    // Atomic write: write to .tmp then replace.
    var tmp = savedPath + ".tmp";
    await File.WriteAllTextAsync(tmp, req.SetupText);
    File.Move(tmp, savedPath, overwrite: true);

    return Results.Ok(new { ok = true, savedPath });
});

// ══ Admin endpoints (all require localhost guard + token) ══════════════════

// ── GET /api/admin/state ──────────────────────────────────────────────────
app.MapGet("/api/admin/state", (
    AgentRuntime runtime,
    AcProcessMonitor monitor,
    MetricsHub metrics) =>
{
    return Results.Ok(new
    {
        isRunning           = runtime.IsRunning,
        startedUtc          = runtime.StartedUtc,
        uptimeSeconds       = runtime.IsRunning ? (DateTime.UtcNow - runtime.StartedUtc).TotalSeconds : 0,
        connectedClients    = runtime.ConnectedClients,
        lastStatusMessage   = runtime.LastStatusMessage,
        physicsHzActual     = runtime.PhysicsHzActual,
        graphicsHzActual    = runtime.GraphicsHzActual,
        acConnected         = runtime.AcConnected,
        acProcessRunning    = monitor.AcProcessRunning,
        carId               = runtime.CarId,
        trackId             = runtime.TrackId,
        agentVersion        = AgentVersion(),
        memoryMb            = metrics.MemoryUsageMB,
        restartCount        = metrics.RestartCount,
    });
});

// ── POST /api/admin/start ─────────────────────────────────────────────────
app.MapPost("/api/admin/start", async (HttpContext ctx, AgentConfigService cfgSvc, AgentRuntime runtime) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    await runtime.StartStreamingAsync();
    return Results.Ok(new { ok = true });
});

// ── POST /api/admin/stop ──────────────────────────────────────────────────
app.MapPost("/api/admin/stop", async (HttpContext ctx, AgentConfigService cfgSvc, AgentRuntime runtime) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    await runtime.StopStreamingAsync();
    return Results.Ok(new { ok = true });
});

// ── GET /api/admin/config ─────────────────────────────────────────────────
app.MapGet("/api/admin/config", (HttpContext ctx, AgentConfigService cfgSvc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    return Results.Ok(cfgSvc.Current);
});

// ── POST /api/admin/config ────────────────────────────────────────────────
app.MapPost("/api/admin/config", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromBody] AgentConfig body) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    await cfgSvc.SaveAsync(body);
    return Results.Ok(new { ok = true });
});

// ── POST /api/admin/restart-required-check ───────────────────────────────
app.MapPost("/api/admin/restart-required-check", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromBody] AgentConfig proposed) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    var cur     = cfgSvc.Current;
    var fields  = new List<string>();
    if (proposed.Port  != cur.Port)  fields.Add("port");
    if (proposed.Token != cur.Token) fields.Add("token");
    return Results.Ok(new { restartRequired = fields.Count > 0, fields });
});

// ── GET /api/admin/metrics ────────────────────────────────────────────────
app.MapGet("/api/admin/metrics", (HttpContext ctx, AgentConfigService cfgSvc, MetricsHub metrics) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    return Results.Ok(new
    {
        wsClientsConnected = metrics.WsClientsConnected,
        framesSentPerSec   = metrics.FramesSentPerSec,
        physicsHzActual    = metrics.PhysicsHzActual,
        graphicsHzActual   = metrics.GraphicsHzActual,
        lastFrameUtc       = metrics.LastFrameUtc,
        avgSendLatencyMs   = metrics.AvgSendLatencyMs,
        droppedFramesCount = metrics.DroppedFramesCount,
        restartCount       = metrics.RestartCount,
        memoryUsageMB      = metrics.MemoryUsageMB,
        uptimeSeconds      = metrics.UptimeSeconds,
    });
});

// ── GET /api/admin/metrics/history ───────────────────────────────────────
app.MapGet("/api/admin/metrics/history", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    MetricsHub metrics,
    [FromQuery] int seconds = 60) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    return Results.Ok(metrics.GetHistory(Math.Clamp(seconds, 1, 3600)));
});

// ── GET /api/admin/logs ───────────────────────────────────────────────────
app.MapGet("/api/admin/logs", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    LogBuffer buffer,
    [FromQuery] int    take     = 200,
    [FromQuery] string? level   = null,
    [FromQuery] string? category = null) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    return Results.Ok(buffer.GetLast(take, level, category));
});

// ── GET /api/admin/logs/download ──────────────────────────────────────────
app.MapGet("/api/admin/logs/download", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    LogBuffer buffer) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    var lines = buffer.GetLast(5000);
    var text  = string.Join('\n', lines.Select(e =>
        $"{e.TimestampUtc:O} [{e.Level,-11}] {e.Category}: {e.Message}"));
    ctx.Response.Headers["Content-Disposition"] =
        $"attachment; filename=\"avo-agent-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log\"";
    return Results.Text(text, "text/plain");
});

// ── POST /api/admin/logs/clear ────────────────────────────────────────────
app.MapPost("/api/admin/logs/clear", (HttpContext ctx, AgentConfigService cfgSvc, LogBuffer buffer) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    buffer.Clear();
    return Results.Ok(new { ok = true });
});

// ── POST /api/admin/diagnostics/run ──────────────────────────────────────
app.MapPost("/api/admin/diagnostics/run", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    AcSharedMemoryReader reader) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();

    bool CanOpenMmf(string name)
    {
        try { using var _ = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read); return true; }
        catch { return false; }
    }

    var docs      = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    var setupRoot = string.IsNullOrWhiteSpace(cfgSvc.Current.Setup.DefaultRoot)
                  ? Path.Combine(docs, "Assetto Corsa", "setups") : cfgSvc.Current.Setup.DefaultRoot;
    bool setupExists   = Directory.Exists(setupRoot);
    bool setupWritable = setupExists && IsDirectoryWritable(setupRoot);

    bool acRunning;
    try { acRunning = Process.GetProcessesByName("acs").Length > 0; }
    catch { acRunning = false; }

    var localIps = Dns.GetHostEntry(Dns.GetHostName()).AddressList
        .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Select(a => a.ToString()).ToArray();

    return Results.Ok(new
    {
        canOpenPhysicsMemory  = CanOpenMmf("acpmf_physics"),
        canOpenGraphicsMemory = CanOpenMmf("acpmf_graphics"),
        canOpenStaticMemory   = CanOpenMmf("acpmf_static"),
        acRunning,
        setupFolder           = setupRoot,
        setupFolderExists     = setupExists,
        setupFolderWritable   = setupWritable,
        tokenConfigured       = cfgSvc.Current.Token != "change-me",
        machineName           = Environment.MachineName,
        localIps,
        httpPort              = cfgSvc.Current.Port,
        discoveryPort         = cfgSvc.Current.Discovery.Port,
    });
});

// ── GET /api/admin/autostart ──────────────────────────────────────────────
app.MapGet("/api/admin/autostart", (HttpContext ctx, AgentConfigService cfgSvc, WindowsAutostartService svc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    return Results.Ok(new { enabled = svc.IsEnabled() });
});

// ── POST /api/admin/autostart/enable ─────────────────────────────────────
app.MapPost("/api/admin/autostart/enable", async (HttpContext ctx, AgentConfigService cfgSvc, WindowsAutostartService svc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    await svc.EnableAsync();
    return Results.Ok(new { ok = true });
});

// ── POST /api/admin/autostart/disable ────────────────────────────────────
app.MapPost("/api/admin/autostart/disable", async (HttpContext ctx, AgentConfigService cfgSvc, WindowsAutostartService svc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    await svc.DisableAsync();
    return Results.Ok(new { ok = true });
});

// ── POST /api/admin/open-folder ───────────────────────────────────────────
app.MapPost("/api/admin/open-folder", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromQuery] string which) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    if (!IsLocal(ctx)) return Results.Forbid();

    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    var folder = which switch
    {
        "setups" => string.IsNullOrWhiteSpace(cfgSvc.Current.Setup.DefaultRoot)
                    ? Path.Combine(docs, "Assetto Corsa", "setups")
                    : cfgSvc.Current.Setup.DefaultRoot,
        "config" => Path.Combine(docs, "AvoTelemetryAgent"),
        "logs"   => Path.Combine(docs, "AvoTelemetryAgent"),
        _        => null,
    };

    if (folder is null) return Results.BadRequest(new { error = "Unknown folder. Use: setups|config|logs" });

    Directory.CreateDirectory(folder);
    Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    return Results.Ok(new { ok = true, folder });
});

// ── Dashboard root redirect ───────────────────────────────────────────────
app.MapGet("/", () => Results.Redirect("/index.html"));

// ── Startup banner ────────────────────────────────────────────────────────
app.Lifetime.ApplicationStarted.Register(() =>
{
    var cfg = app.Services.GetRequiredService<AgentConfigService>().Current;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║       AvoTelemetryAgent              ║");
    Console.WriteLine($"║  v{AgentVersion(),-34}║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine($"  Dashboard  : http://localhost:{cfg.Port}");
    Console.WriteLine($"  WebSocket  : ws://localhost:{cfg.Port}/ws?token=***");
    Console.WriteLine($"  Ping       : http://localhost:{cfg.Port}/api/ping");
    Console.WriteLine($"  Info       : http://localhost:{cfg.Port}/api/info");
    Console.WriteLine($"  Setup      : POST http://localhost:{cfg.Port}/api/setup/apply");
    Console.WriteLine($"  Admin      : http://localhost:{cfg.Port}/api/admin/state");
    Console.WriteLine($"  Metrics    : http://localhost:{cfg.Port}/api/admin/metrics");
    Console.WriteLine($"  Logs       : http://localhost:{cfg.Port}/api/admin/logs");
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Keyboard shortcuts (dashboard): S=start/stop  D=diagnostics  L=logs");
    Console.ResetColor();
    Console.WriteLine();
});

app.Run();

// ══ Helpers ═══════════════════════════════════════════════════════════════════

static string AgentVersion()
    => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

/// <summary>Constant-time token comparison using UTF-8 encoded bytes.</summary>
static bool TokenOk(HttpContext ctx, AgentConfigService cfgSvc)
{
    var provided = ctx.Request.Headers["X-AVO-TOKEN"].FirstOrDefault()
                ?? ctx.Request.Query["token"].FirstOrDefault()
                ?? string.Empty;
    var expected = cfgSvc.Current.Token;
    var a = Encoding.UTF8.GetBytes(provided);
    var b = Encoding.UTF8.GetBytes(expected);
    // When lengths differ the result is always false.
    // We still call FixedTimeEquals on padded equal-length buffers so the
    // execution time does not reveal the expected token length to a remote
    // observer (timing side-channel mitigation).
    if (a.Length != b.Length)
    {
        var maxLen = Math.Max(a.Length, b.Length);
        var pa = new byte[maxLen]; a.CopyTo(pa, 0);
        var pb = new byte[maxLen]; b.CopyTo(pb, 0);
        CryptographicOperations.FixedTimeEquals(pa, pb); // consume constant time
        return false;
    }
    return CryptographicOperations.FixedTimeEquals(a, b);
}

static bool IsLocal(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress;
    return ip is null || IPAddress.IsLoopback(ip);
}

static bool IsAdminPath(PathString path)
    => path.StartsWithSegments("/api/admin")
    || path == "/"
    || path == "/index.html"
    || path.StartsWithSegments("/app.");

static string? SanitiseSegment(string? segment)
{
    if (string.IsNullOrWhiteSpace(segment)) return null;
    if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
    if (segment is "." or "..") return null;
    return segment;
}

static bool IsDirectoryWritable(string dir)
{
    try
    {
        var tmp = Path.Combine(dir, Path.GetRandomFileName());
        File.WriteAllText(tmp, "");
        File.Delete(tmp);
        return true;
    }
    catch { return false; }
}

// ── Request models ────────────────────────────────────────────────────────────
record SetupApplyRequest(
    string? CarId,
    string? TrackId,
    string? FileName,
    string? SetupText,
    string? RelativePathOptional);

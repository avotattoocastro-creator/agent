using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AvoTelemetryAgent.Services;
using AvoTelemetryAgent.SharedMemory;
using AvoTelemetryAgent.UI;
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
builder.Services.AddSingleton<SetupReferenceService>();
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
            if (!IsLocalOrSelf(ctx))
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

// ── GET /api/public/auth-info ─────────────────────────────────────────────
app.MapGet("/api/public/auth-info", () =>
    Results.Ok(new { tokenRequired = true }));

// ── POST /api/setup/apply  (kept for backward compatibility) ─────────────────
app.MapPost("/api/setup/apply", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromBody] SetupApplyRequest req) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    return await ExecuteSetupSave(
        cfgSvc, req.CarId, req.TrackId, req.FileName, req.SetupText,
        overwrite: true, relPath: req.RelativePathOptional);
});

// ── POST /api/setup/save ──────────────────────────────────────────────────────
app.MapPost("/api/setup/save", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromBody] SetupSaveRequest req) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    return await ExecuteSetupSave(
        cfgSvc, req.CarId, req.TrackId, req.FileName, req.SetupText,
        req.Overwrite, relPath: null);
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
        tokenConfigured       = cfgSvc.Current.Token != "12345",
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
    if (!IsLocalOrSelf(ctx)) return Results.Forbid();

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

// ══ Reference Root admin endpoints (localhost + token) ═══════════════════════

// ── POST /api/admin/referenceRoot/browse ──────────────────────────────────────
app.MapPost("/api/admin/referenceRoot/browse", (
    HttpContext ctx,
    AgentConfigService cfgSvc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    if (!IsLocalOrSelf(ctx))   return Results.Forbid();

    if (!cfgSvc.Current.Setup.AllowBrowseDialog)
        return Results.BadRequest(new
        {
            error = "Browse dialog is disabled. Set ReferenceRoot manually via the /set endpoint.",
        });

    try
    {
        var picked = NativeFolderPicker.PickFolder("Select Reference Setups Folder");
        return picked is null
            ? Results.Ok(new { ok = false, path = (string?)null })
            : Results.Ok(new { ok = true,  path = picked });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new
        {
            error = $"Dialog unavailable (headless?). Set path manually. Detail: {ex.Message}",
        });
    }
});

// ── POST /api/admin/referenceRoot/set ─────────────────────────────────────────
app.MapPost("/api/admin/referenceRoot/set", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc,
    [FromBody] ReferenceRootSetRequest req) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Path))
        return Results.BadRequest(new { error = "path is required." });

    if (!Directory.Exists(req.Path))
        return Results.BadRequest(new { error = "Folder does not exist." });

    var updated = cfgSvc.Current;
    // Replace the setup section in-place via a new SetupSection instance.
    updated.Setup = new SetupSection
    {
        DefaultRoot      = cfgSvc.Current.Setup.DefaultRoot,
        ReferenceRoot    = req.Path,
        AllowBrowseDialog = cfgSvc.Current.Setup.AllowBrowseDialog,
    };
    await cfgSvc.SaveAsync(updated);
    refSvc.Rescan();
    return Results.Ok(new { ok = true, path = req.Path });
});

// ── GET /api/admin/referenceRoot/get ──────────────────────────────────────────
app.MapGet("/api/admin/referenceRoot/get", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    var path = cfgSvc.Current.Setup.ReferenceRoot;
    return Results.Ok(new
    {
        ok         = true,
        path,
        configured = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path),
        carsCount  = refSvc.CarsCount,
        totalCount = refSvc.TotalCount,
    });
});

// ── POST /api/admin/setup/reference/rescan ────────────────────────────────────
app.MapPost("/api/admin/setup/reference/rescan", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    refSvc.Rescan();
    return Results.Ok(new { ok = true, count = refSvc.TotalCount });
});

// ══ Public reference browsing endpoints (LAN allowed, token required) ════════

// ── GET /api/reference/root ───────────────────────────────────────────────────
app.MapGet("/api/reference/root", (
    HttpContext ctx,
    AgentConfigService cfgSvc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    var path = cfgSvc.Current.Setup.ReferenceRoot;
    return Results.Ok(new
    {
        ok         = true,
        configured = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path),
        path,
    });
});

// ── GET /api/reference/cars ───────────────────────────────────────────────────
app.MapGet("/api/reference/cars", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    if (!IsRefRootConfigured(cfgSvc))
        return Results.Ok(Array.Empty<string>());

    return Results.Ok(refSvc.GetCars());
});

// ── GET /api/reference/tracks?car=CARFOLDER ───────────────────────────────────
app.MapGet("/api/reference/tracks", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc,
    [FromQuery] string? car) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(car) || !IsValidRefSegment(car))
        return Results.BadRequest(new { error = "Valid car parameter is required." });

    return Results.Ok(refSvc.GetTracks(car));
});

// ── GET /api/reference/setups?car=CARFOLDER&track=TRACKFOLDER ────────────────
app.MapGet("/api/reference/setups", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc,
    [FromQuery] string? car,
    [FromQuery] string? track) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(car)   || !IsValidRefSegment(car))
        return Results.BadRequest(new { error = "Valid car parameter is required." });
    if (string.IsNullOrWhiteSpace(track) || !IsValidRefSegment(track))
        return Results.BadRequest(new { error = "Valid track parameter is required." });

    var items = refSvc.GetSetups(car, track);
    var fileNames = items.Select(i => i.FileName).OrderBy(x => x).ToList();
    return Results.Ok(fileNames);
});

// ── GET /api/reference/setup/read?car=...&track=...&file=... ─────────────────
app.MapGet("/api/reference/setup/read", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromQuery] string? car,
    [FromQuery] string? track,
    [FromQuery] string? file) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(car)   || !IsValidRefSegment(car))
        return Results.BadRequest(new { error = "Valid car parameter is required." });
    if (string.IsNullOrWhiteSpace(track) || !IsValidRefSegment(track))
        return Results.BadRequest(new { error = "Valid track parameter is required." });
    if (string.IsNullOrWhiteSpace(file)  || !IsValidRefIniFile(file))
        return Results.BadRequest(new { error = "Valid .ini file name is required." });

    var root = cfgSvc.Current.Setup.ReferenceRoot;
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        return Results.BadRequest(new { error = "ReferenceRoot is not configured." });

    var absPath = SafeRefPath(root, car, track, file);
    if (absPath is null || !File.Exists(absPath))
        return Results.NotFound(new { error = "Setup file not found." });

    var fi = new FileInfo(absPath);
    if (fi.Length > 512 * 1024)
        return Results.BadRequest(new { error = "File exceeds 512 KB limit." });

    var text = await File.ReadAllTextAsync(absPath);
    return Results.Ok(new { ok = true, fileName = file, setupText = text });
});

// ── GET /api/setups/reference/list?carId=...&trackId=... ─────────────────────
app.MapGet("/api/setups/reference/list", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc,
    [FromQuery] string? carId   = null,
    [FromQuery] string? trackId = null) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    var items = refSvc.GetAllItems(carId, trackId);
    return Results.Ok(new { ok = true, items });
});

// ── GET /api/setups/reference/tree ────────────────────────────────────────────
app.MapGet("/api/setups/reference/tree", (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    var cars = refSvc.GetCars();
    var tree = cars.Select(carId => new
    {
        carId,
        tracks = refSvc.GetTracks(carId).Select(trackId => new
        {
            trackId,
            files = refSvc.GetSetups(carId, trackId).Select(s => new
            {
                s.FileName, s.DisplayName, s.UpdatedUtc, s.SizeBytes,
            }),
        }),
    });
    return Results.Ok(new { ok = true, cars = tree });
});


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
    Console.WriteLine($"  Setup Save : POST http://localhost:{cfg.Port}/api/setup/save");
    Console.WriteLine($"  Ref Root   : GET  http://localhost:{cfg.Port}/api/reference/root");
    Console.WriteLine($"  Ref Cars   : GET  http://localhost:{cfg.Port}/api/reference/cars");
    Console.WriteLine($"  Ref Tree   : GET  http://localhost:{cfg.Port}/api/setups/reference/tree");
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
    var provided = ctx.Request.Headers["X-API-TOKEN"].FirstOrDefault()
                ?? ctx.Request.Headers["X-AVO-TOKEN"].FirstOrDefault()
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

static bool IsLocalOrSelf(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress;
    if (ip is null || IPAddress.IsLoopback(ip)) return true;
    // Map IPv4-in-IPv6 (::ffff:x.x.x.x) to its plain IPv4 form for comparison.
    var check = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    return LocalAddressCache.Contains(check);
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

// ── Reference-root security helpers ──────────────────────────────────────────

/// <summary>
/// Returns true when the segment is safe for use as a folder name component.
/// Allows only [A-Za-z0-9 _-] — no path separators, no dots, no empty string.
/// </summary>
static bool IsValidRefSegment(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return false;
    foreach (var c in s)
        if (!char.IsLetterOrDigit(c) && c != ' ' && c != '_' && c != '-')
            return false;
    return true;
}

/// <summary>
/// Returns true when the file name is a valid setup file.
/// Base name must match [A-Za-z0-9 _-] and the extension must be .ini or .json.
/// </summary>
static bool IsValidRefIniFile(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return false;
    // Determine accepted extension (.ini or .json)
    int extLen;
    if      (s.EndsWith(".ini",  StringComparison.OrdinalIgnoreCase)) extLen = ".ini".Length;
    else if (s.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) extLen = ".json".Length;
    else return false;
    var baseName = s[..^extLen];
    if (baseName.Length == 0) return false;
    foreach (var c in baseName)
        if (!char.IsLetterOrDigit(c) && c != ' ' && c != '_' && c != '-')
            return false;
    return true;
}

/// <summary>
/// Combines <paramref name="root"/> with the given segments and verifies the
/// result is still inside <paramref name="root"/>. Returns null on traversal.
/// </summary>
static string? SafeRefPath(string root, params string[] segments)
{
    var combined = Path.Combine([root, .. segments]);
    var resolved = Path.GetFullPath(combined);
    var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
    return resolved.StartsWith(rootFull, StringComparison.Ordinal) ? resolved : null;
}

static bool IsRefRootConfigured(AgentConfigService cfgSvc)
{
    var r = cfgSvc.Current.Setup.ReferenceRoot;
    return !string.IsNullOrWhiteSpace(r) && Directory.Exists(r);
}

// ── Shared setup-save logic ───────────────────────────────────────────────────

static async Task<IResult> ExecuteSetupSave(
    AgentConfigService cfgSvc,
    string? carId, string? trackId, string? fileName, string? setupText,
    bool overwrite, string? relPath)
{
    if (string.IsNullOrWhiteSpace(carId)    ||
        string.IsNullOrWhiteSpace(trackId)  ||
        string.IsNullOrWhiteSpace(fileName) ||
        string.IsNullOrWhiteSpace(setupText))
        return Results.BadRequest(new { error = "carId, trackId, fileName and setupText are required." });

    var safeFileName = SanitiseSegment(fileName);
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
    if (!string.IsNullOrWhiteSpace(relPath))
    {
        var resolved = Path.GetFullPath(Path.Combine(root, relPath));
        if (!resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid relative path." });
        savedPath = resolved;
    }
    else
    {
        var safeCarId   = SanitiseSegment(carId);
        var safeTrackId = SanitiseSegment(trackId);
        if (safeCarId is null || safeTrackId is null)
            return Results.BadRequest(new { error = "Invalid carId or trackId." });
        var dir = Path.Combine(root, safeCarId, safeTrackId);
        savedPath = Path.Combine(dir, safeFileName);
        if (!Path.GetFullPath(savedPath).StartsWith(
                Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Resolved path escapes setup directory." });
    }

    if (!overwrite && File.Exists(savedPath))
        return Results.Conflict(new { error = "File already exists. Set overwrite=true to replace." });

    Directory.CreateDirectory(Path.GetDirectoryName(savedPath)!);
    var tmp = savedPath + ".tmp";
    await File.WriteAllTextAsync(tmp, setupText);
    File.Move(tmp, savedPath, overwrite: true);
    return Results.Ok(new { ok = true, savedPath });
}

// ── Request models ────────────────────────────────────────────────────────────
record SetupApplyRequest(
    string? CarId,
    string? TrackId,
    string? FileName,
    string? SetupText,
    string? RelativePathOptional);

record SetupSaveRequest(
    string? CarId,
    string? TrackId,
    string? FileName,
    string? SetupText,
    bool    Overwrite = true);

record ReferenceRootSetRequest(string? Path);

/// <summary>
/// Caches the machine's own unicast IP addresses, refreshed every 30 seconds.
/// Avoids enumerating NICs on every admin request.
/// </summary>
static class LocalAddressCache
{
    private static HashSet<IPAddress> _cache = Build();
    private static DateTime _builtAt = DateTime.UtcNow;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public static bool Contains(IPAddress addr)
    {
        if (DateTime.UtcNow - _builtAt > Ttl)
        {
            _cache   = Build();
            _builtAt = DateTime.UtcNow;
        }
        return _cache.Contains(addr);
    }

    private static HashSet<IPAddress> Build()
        => new(NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address.IsIPv4MappedToIPv6 ? u.Address.MapToIPv4() : u.Address));
}

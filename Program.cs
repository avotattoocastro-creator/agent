using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvoTelemetryAgent.Services;
using AvoTelemetryAgent.SharedMemory;
using AvoTelemetryAgent.UI;
using Microsoft.AspNetCore.Mvc;

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

// ── Services ──────────────────────────────────────────────────────────────
// Register LogBuffer as a DI singleton first; then expose the SAME instance
// as ILoggerProvider so ASP.NET Core's LoggerFactory captures all ILogger calls.
builder.Services.AddSingleton<LogBuffer>();
builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<LogBuffer>());
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
builder.Services.AddSingleton<LogWebSocketStreamer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentRuntime>());
builder.Services.AddHostedService<WatchdogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcProcessMonitor>());
builder.Services.AddHostedService<LanDiscoveryService>();

// ── CORS (allow any origin – LAN tool, further guarded by token) ────────────
builder.Services.AddCors(o => o.AddPolicy("public-api", p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ── Kestrel ───────────────────────────────────────────────────────────────
builder.WebHost.UseUrls($"http://0.0.0.0:{configSvc.Current.Port}");

var app = builder.Build();

// ── Static files (wwwroot → dashboard) ───────────────────────────────────
app.UseStaticFiles();

// ── CORS ─────────────────────────────────────────────────────────────────
app.UseCors("public-api");

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

// ── /ws/logs ──────────────────────────────────────────────────────────────
app.Map("/ws/logs", async (HttpContext ctx, LogWebSocketStreamer logs, AgentConfigService cfgSvc) =>
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
    var tail = int.TryParse(ctx.Request.Query["tail"], out var t) ? Math.Clamp(t, 0, 2000) : 200;
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    app.Logger.LogInformation("WS/LOGS CONNECTED");
    await logs.HandleAsync(ws, ctx.RequestAborted, tail);
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

// ── POST /api/setup/apply ─────────────────────────────────────────────────────
app.MapPost("/api/setup/apply", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    ILogger<Program> logger,
    [FromBody] SetupApplyRequest req) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Car)        ||
        string.IsNullOrWhiteSpace(req.Track)      ||
        string.IsNullOrWhiteSpace(req.File)       ||
        string.IsNullOrWhiteSpace(req.IniContent))
        return Results.BadRequest(new { error = "car, track, file and iniContent are required." });

    var safeCar   = SanitiseSegment(req.Car);
    var safeTrack = SanitiseSegment(req.Track);
    var safeFile  = SanitiseSegment(req.File);
    if (safeCar is null || safeTrack is null || safeFile is null)
        return Results.BadRequest(new { error = "Invalid car, track, or file name." });

    // Prefer ReferenceRoot (same root as /api/reference/*), fall back to DefaultRoot.
    var root = cfgSvc.Current.Setup.ReferenceRoot;
    if (string.IsNullOrWhiteSpace(root))
        root = cfgSvc.Current.Setup.DefaultRoot;
    if (string.IsNullOrWhiteSpace(root))
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        root = Path.Combine(docs, "Assetto Corsa", "setups");
    }

    var tag = string.IsNullOrWhiteSpace(req.Tag) ? "AI" : req.Tag.Trim();
    var safeTag = SanitiseSegment(tag) ?? "AI";
    string savedFile;
    if (req.Versioned)
    {
        var baseName = Path.GetFileNameWithoutExtension(safeFile);
        var ts       = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        savedFile    = $"{baseName}__{safeTag}_{ts}.ini";
    }
    else
    {
        savedFile = safeFile.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
            ? safeFile : safeFile + ".ini";
    }

    var dir     = Path.Combine(root, safeCar, safeTrack);
    var absPath = Path.GetFullPath(Path.Combine(dir, savedFile));
    if (!absPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Resolved path escapes setup directory." });

    logger.LogInformation(
        "SAVE REQ car={Car} track={Track} file={File} versioned={Versioned} bytes={Bytes}",
        req.Car, req.Track, savedFile, req.Versioned, req.IniContent.Length);

    try
    {
        Directory.CreateDirectory(dir);
        var tmp = absPath + ".tmp";
        await File.WriteAllTextAsync(tmp, req.IniContent);
        File.Move(tmp, absPath, overwrite: true);
        logger.LogInformation("SAVE OK path={Path}", absPath);
        return Results.Ok(new { ok = true, savedFile, path = absPath });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "SAVE ERR");
        return Results.Problem(ex.Message);
    }
});

// ── POST /api/setup/save ──────────────────────────────────────────────────────
app.MapPost("/api/setup/save", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    ILogger<Program> logger,
    [FromBody] SetupSaveRequest req) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    return await ExecuteSetupSave(
        cfgSvc, logger, req.CarId, req.TrackId, req.FileName, req.SetupText,
        req.Overwrite, relPath: null, versioned: req.Versioned);
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
    // Allow unauthenticated reads from localhost so the dashboard can bootstrap
    // itself even before the user has pasted their token — but only when the
    // admin has explicitly restricted the UI to localhost (BindLocalhostOnly).
    if (!TokenOk(ctx, cfgSvc) && !(cfgSvc.Current.AdminUi.BindLocalhostOnly && IsLocalOrSelf(ctx)))
        return Results.Unauthorized();
    var c = cfgSvc.Current;
    return Results.Ok(new
    {
        // token is NEVER returned in full — only a flag so the UI can indicate status
        tokenConfigured = !string.IsNullOrWhiteSpace(c.Token) && c.Token != AgentConfig.DefaultToken,
        port            = c.Port,
        physicsHz       = c.PhysicsHz,
        graphicsHz      = c.GraphicsHz,
        staticHz        = c.StaticHz,
        setup           = c.Setup,
        discovery       = c.Discovery,
        adminUi         = c.AdminUi,
        agent           = c.Agent,
    });
});

// ── POST /api/admin/config ────────────────────────────────────────────────
app.MapPost("/api/admin/config", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromBody] AgentConfig body) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    // Never overwrite the token with an empty value (e.g. when the dashboard
    // omits the token field because it is not shown in plain text).
    if (string.IsNullOrWhiteSpace(body.Token))
        body.Token = cfgSvc.Current.Token;
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
        tokenConfigured       = !string.IsNullOrWhiteSpace(cfgSvc.Current.Token) && cfgSvc.Current.Token != AgentConfig.DefaultToken,
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

// ── POST /api/reference/setup/apply ──────────────────────────────────────────
app.MapPost("/api/reference/setup/apply", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    AgentRuntime runtime,
    LogBuffer logBuf,
    ILogger<Program> logger,
    [FromBody] ApplySetupRequestDto req) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();

    // ── Detailed diagnostic logging of the incoming request ──────────────────
    var reqChangeSummary = req.Changes is { Count: > 0 }
        ? string.Join(", ", req.Changes.Select(c => $"[{c.Section}]{c.Key}"))
        : "(none)";
    logBuf.Add(LogLevel.Information, "WebUI",
        $"APPLY REQUEST car={req.Car} track={req.Track} baseFile={req.BaseFile} " +
        $"changes={req.Changes?.Count ?? 0} ({reqChangeSummary}) " +
        $"versioned={req.CreateVersionedCopy}");
    logBuf.Add(LogLevel.Information, "WebUI",
        $"AGENT STATE isRunning={runtime.IsRunning} " +
        $"acConnected={runtime.AcConnected} " +
        $"activeCar={runtime.CarId} activeTrack={runtime.TrackId}");

    if (string.IsNullOrWhiteSpace(req.Car)      || !IsValidRefSegment(req.Car))
        return Results.BadRequest(new { error = "Valid car is required." });
    if (string.IsNullOrWhiteSpace(req.Track)    || !IsValidRefSegment(req.Track))
        return Results.BadRequest(new { error = "Valid track is required." });
    if (string.IsNullOrWhiteSpace(req.BaseFile) || !IsValidRefIniFile(req.BaseFile))
        return Results.BadRequest(new { error = "Valid baseFile (.ini) is required." });
    if (req.Changes is null || req.Changes.Count == 0)
        return Results.BadRequest(new { error = "At least one change is required." });

    // ── Agent / Shared Memory state diagnostics (informational, not blocking) ─
    if (!runtime.IsRunning)
    {
        logBuf.Add(LogLevel.Warning, "WebUI",
            "APPLY WARNING: Agent not running — setup will still be written to disk.");
    }
    if (!runtime.AcConnected)
    {
        logBuf.Add(LogLevel.Warning, "WebUI",
            "APPLY WARNING: Shared Memory not connected — cannot verify active car.");
    }
    // Car mismatch check — warn but do not block (file-based apply is offline-capable).
    var activeCar = runtime.CarId;
    if (!string.IsNullOrWhiteSpace(activeCar) &&
        !activeCar.Equals(req.Car, StringComparison.OrdinalIgnoreCase))
    {
        logBuf.Add(LogLevel.Warning, "WebUI",
            $"APPLY WARNING: Car mismatch — requested '{req.Car}' but active car is '{activeCar}'.");
    }

    var root = cfgSvc.Current.Setup.ReferenceRoot;
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        return Results.BadRequest(new { error = "ReferenceRoot is not configured." });

    var baseFilePath = SafeRefPath(root, req.Car, req.Track, req.BaseFile);
    if (baseFilePath is null || !File.Exists(baseFilePath))
    {
        logBuf.Add(LogLevel.Error, "WebUI",
            $"APPLY ERROR: Base file not found — path={baseFilePath ?? "(invalid)"}");
        return Results.NotFound(new { error = "Base file not found.", path = baseFilePath });
    }

    var dir      = Path.Combine(root, req.Car, req.Track);
    var baseName = Path.GetFileNameWithoutExtension(req.BaseFile);
    var savedFile = req.CreateVersionedCopy
        ? MakeVersionedFileName(dir, baseName)
        : req.BaseFile;

    var absPath = SafeRefPath(root, req.Car, req.Track, savedFile);
    if (absPath is null)
        return Results.BadRequest(new { error = "Resolved path escapes reference root." });

    logBuf.Add(LogLevel.Information, "WebUI",
        $"APPLY TARGET path={absPath}");

    logger.LogInformation(
        "SAVE REQ car={Car} track={Track} file={File} versioned={Versioned} changes={Changes}",
        req.Car, req.Track, savedFile, req.CreateVersionedCopy, req.Changes.Count);

    try
    {
        var originalText = await File.ReadAllTextAsync(baseFilePath);
        var before       = ParseIniSections(originalText);

        // ── Log available parameters vs requested (capped to avoid huge entries) ─
        const int MaxLoggedParams = 30;
        var availableParams = before
            .Where(s => s.Key != string.Empty)
            .SelectMany(s => s.Value.Keys.Select(k => $"[{s.Key}]{k}"))
            .ToList();
        var availSummary = availableParams.Count > MaxLoggedParams
            ? string.Join(", ", availableParams.Take(MaxLoggedParams)) + $", … (+{availableParams.Count - MaxLoggedParams} more)"
            : string.Join(", ", availableParams);
        logBuf.Add(LogLevel.Information, "WebUI",
            $"APPLY AVAILABLE PARAMS ({availableParams.Count}): {availSummary}");

        // ── Check each requested parameter against available ones ─────────────
        var missingParams = req.Changes
            .Where(c => !before.TryGetValue(c.Section, out var sec) || !sec.ContainsKey(c.Key))
            .Select(c => $"[{c.Section}]{c.Key}")
            .ToList();
        if (missingParams.Count > 0)
        {
            logBuf.Add(LogLevel.Warning, "WebUI",
                $"APPLY WARNING: Parameter(s) not found in setup (will be appended): {string.Join(", ", missingParams)}");
        }

        var patched      = PatchIni(originalText, req.Changes);
        var after        = ParseIniSections(patched);

        // Build per-change diff (oldValue is null when the key is new).
        var diff = req.Changes.Select(c =>
        {
            var oldVal = before.TryGetValue(c.Section, out var bs) && bs.TryGetValue(c.Key, out var ov) ? ov : (string?)null;
            var newVal = after.TryGetValue(c.Section, out var afs) && afs.TryGetValue(c.Key, out var nv) ? nv : (string?)null;
            return new { section = c.Section, key = c.Key, oldValue = oldVal, newValue = newVal };
        }).ToList();

        Directory.CreateDirectory(dir);
        var tmp = absPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, patched);
            File.Move(tmp, absPath, overwrite: !req.CreateVersionedCopy);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* best effort */ }
        }

        if (req.CreateVersionedCopy)
        {
            var vLabel = savedFile.Contains("__v") ? savedFile.Split("__v").Last().Replace(".ini", "") : "001";
            logger.LogInformation("VERSIONED SAVE: savedFile={SavedFile} v={Version}", savedFile, vLabel);

            // Save metadata JSON alongside the INI — failure must never block the response.
            _ = Task.Run(async () =>
            {
                try
                {
                    var meta     = new SetupSaveMetadata(req.BaseFile, savedFile,
                        DateTime.UtcNow.ToString("O"), req.Car, req.Track, req.Changes,
                        req.Reason, "AvoPerformanceSetupAI", AgentVersion());
                    var metaJson = JsonSerializer.Serialize(meta,
                        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    await File.WriteAllTextAsync(Path.ChangeExtension(absPath, ".meta.json"), metaJson);
                }
                catch (Exception ex) { logger.LogWarning("META WRITE FAILED: {Msg}", ex.Message); }
            });
        }

        logger.LogInformation("SAVE OK path={Path}", absPath);
        logBuf.Add(LogLevel.Information, "WebUI", $"APPLY OK savedFile={savedFile} path={absPath}");
        return Results.Ok(new { ok = true, savedFile, path = absPath, wroteVersioned = req.CreateVersionedCopy, diff });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "SAVE ERR ex={Message}", ex.Message);
        logBuf.Add(LogLevel.Error, "WebUI",
            $"APPLY FAIL: {ex.GetType().Name}: {ex.Message}");
        return Results.Ok(new { ok = false, error = ex.GetType().Name, details = ex.Message });
    }
});

// ── GET /api/reference/setup/versions?car=...&track=...&baseFile=... ──────────
app.MapGet("/api/reference/setup/versions", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    [FromQuery] string? car,
    [FromQuery] string? track,
    [FromQuery] string? baseFile) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(car)      || !IsValidRefSegment(car))
        return Results.BadRequest(new { error = "Valid car is required." });
    if (string.IsNullOrWhiteSpace(track)    || !IsValidRefSegment(track))
        return Results.BadRequest(new { error = "Valid track is required." });
    if (string.IsNullOrWhiteSpace(baseFile) || !IsValidRefIniFile(baseFile))
        return Results.BadRequest(new { error = "Valid baseFile (.ini) is required." });

    var root = cfgSvc.Current.Setup.ReferenceRoot;
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        return Results.BadRequest(new { error = "ReferenceRoot is not configured." });

    // Use a dummy filename to derive the directory safely.
    var probe   = SafeRefPath(root, car, track, "x.ini");
    if (probe is null) return Results.BadRequest(new { error = "Invalid path." });
    var dirPath = Path.GetDirectoryName(probe)!;

    if (!Directory.Exists(dirPath))
        return Results.Ok(new { ok = true, versions = Array.Empty<object>() });

    var baseName = Path.GetFileNameWithoutExtension(baseFile);
    var iniFiles = Directory.GetFiles(dirPath, $"{baseName}__AI__*.ini", SearchOption.TopDirectoryOnly)
        .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f)
        .ToList();

    var versions = new List<object>();
    foreach (var iniPath in iniFiles)
    {
        var fi       = new FileInfo(iniPath);
        object? meta = null;
        var metaPath = Path.ChangeExtension(iniPath, ".meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                var txt = await File.ReadAllTextAsync(metaPath);
                meta = JsonSerializer.Deserialize<object>(txt);
            }
            catch { /* metadata is optional */ }
        }
        versions.Add(new { fileName = fi.Name, savedUtc = fi.LastWriteTimeUtc, sizeBytes = fi.Length, meta });
    }

    return Results.Ok(new { ok = true, versions });
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

// ── POST /api/setups/save ─────────────────────────────────────────────────────
app.MapPost("/api/setups/save", async (
    HttpContext ctx,
    AgentConfigService cfgSvc,
    SetupReferenceService refSvc,
    [FromBody] SetupVersionedSaveRequest req) =>
{
    if (!TokenOk(ctx, cfgSvc)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Car)   || !IsValidRefSegment(req.Car))
        return Results.BadRequest(new { error = "Valid car is required." });
    if (string.IsNullOrWhiteSpace(req.Track) || !IsValidRefSegment(req.Track))
        return Results.BadRequest(new { error = "Valid track is required." });
    if (string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest(new { error = "content is required." });

    var root = cfgSvc.Current.Setup.ReferenceRoot;
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        return Results.BadRequest(new { error = "ReferenceRoot is not configured." });

    var baseName = string.IsNullOrWhiteSpace(req.BaseFileName) ? "iter" : req.BaseFileName.Trim();
    if (!IsValidRefSegment(baseName))
        return Results.BadRequest(new { error = "Invalid baseFileName." });

    var safeCar   = SanitiseSegment(req.Car);
    var safeTrack = SanitiseSegment(req.Track);
    if (safeCar is null || safeTrack is null)
        return Results.BadRequest(new { error = "Invalid car or track." });

    var dir = Path.GetFullPath(Path.Combine(root, safeCar, safeTrack));
    if (!dir.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Resolved path escapes reference root." });

    Directory.CreateDirectory(dir);
    var fileName = NextVersionedFileName(dir, baseName);
    var absPath  = Path.Combine(dir, fileName);

    try
    {
        var tmp = absPath + ".tmp";
        await File.WriteAllTextAsync(tmp, req.Content);
        File.Move(tmp, absPath, overwrite: false);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "File already exists (concurrent write). Please retry." });
    }

    refSvc.Rescan();
    return Results.Ok(new { ok = true, fileName });
});

// ── Startup banner ────────────────────────────────────────────────────────
app.Lifetime.ApplicationStarted.Register(() =>
{
    var cfg = app.Services.GetRequiredService<AgentConfigService>().Current;
    app.Logger.LogInformation("Agent started on port {Port}", cfg.Port);
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║       AvoTelemetryAgent              ║");
    Console.WriteLine($"║  v{AgentVersion(),-34}║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine($"  Dashboard  : http://localhost:{cfg.Port}");
    Console.WriteLine($"  WebSocket  : ws://localhost:{cfg.Port}/ws?token=***");
    Console.WriteLine($"  Log Stream : ws://localhost:{cfg.Port}/ws/logs?token=***");
    Console.WriteLine($"  Ping       : http://localhost:{cfg.Port}/api/ping");
    Console.WriteLine($"  Info       : http://localhost:{cfg.Port}/api/info");
    Console.WriteLine($"  Setup      : POST http://localhost:{cfg.Port}/api/setup/apply");
    Console.WriteLine($"  Setup Save : POST http://localhost:{cfg.Port}/api/setup/save");
    Console.WriteLine($"  Setup SaveV: POST http://localhost:{cfg.Port}/api/setups/save");
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

/// <summary>
/// Returns a filename for <paramref name="baseName"/> that does not yet
/// exist in <paramref name="dir"/>. Tries base.ini first, then
/// base_v2.ini, base_v3.ini … up to 9999.
/// </summary>
static string NextVersionedFileName(string dir, string baseName)
{
    var candidate = baseName + ".ini";
    if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
    for (int v = 2; v <= 9999; v++)
    {
        candidate = $"{baseName}_v{v}.ini";
        if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
    }
    return $"{baseName}_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.ini";
}

/// <summary>
/// Builds a versioned filename: <c>{baseName}__AI__{yyyyMMdd_HHmmss}__v{NNN}.ini</c>.
/// The zero-padded increment reflects how many existing versioned copies are already
/// in <paramref name="dir"/> so callers can see the history at a glance.
/// </summary>
static string MakeVersionedFileName(string dir, string baseName)
{
    var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
    int existing = Directory.Exists(dir)
        ? Directory.GetFiles(dir, $"{baseName}__AI__*.ini", SearchOption.TopDirectoryOnly).Length
        : 0;
    var v = (existing + 1).ToString("D3");
    return $"{baseName}__AI__{ts}__v{v}.ini";
}

// ── INI patch helper ──────────────────────────────────────────────────────────
/// <summary>
/// Applies a list of Section/Key/Value changes to raw INI text.
/// If a matching key is found in the right section it is updated in-place.
/// If not found, the key is appended at the end of the section (or a new
/// section + key is added at the end of the file).
/// </summary>
static string PatchIni(string original, IEnumerable<ApplySetupChangeDto> changes)
{
    // Detect and preserve the original line-ending style.
    var sep   = original.Contains("\r\n") ? "\r\n" : "\n";
    var lines = new List<string>(original.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));

    foreach (var change in changes)
    {
        var sectionHeader = $"[{change.Section}]";
        int sectionLine   = -1;

        // Find section.
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionLine = i;
                break;
            }
        }

        if (sectionLine >= 0)
        {
            // Look for the key inside this section (stop at next section header).
            bool found = false;
            for (int i = sectionLine + 1; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimEnd();
                if (trimmed.StartsWith('[')) break; // next section
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0) continue;
                var existingKey = trimmed[..eqIdx].Trim();
                if (existingKey.Equals(change.Key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{change.Key}={change.Value}";
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                // Append key at end of section (before next section or EOF).
                int insertAt = sectionLine + 1;
                while (insertAt < lines.Count && !lines[insertAt].TrimEnd().StartsWith('['))
                    insertAt++;
                lines.Insert(insertAt, $"{change.Key}={change.Value}");
            }
        }
        else
        {
            // Section doesn't exist — add it at the end.
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);
            lines.Add(sectionHeader);
            lines.Add($"{change.Key}={change.Value}");
        }
    }

    return string.Join(sep, lines);
}

// ── INI section parser ────────────────────────────────────────────────────────
/// <summary>
/// Parses raw INI text into a two-level dictionary [section][key] = value.
/// Keys in the header (before the first section) are stored under the empty string key.
/// Comment lines (starting with ; or #) are ignored.
/// </summary>
static Dictionary<string, Dictionary<string, string>> ParseIniSections(string text)
{
    var result  = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    var section = string.Empty;
    result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
        if (line[0] == '[' && line[^1] == ']')
        {
            section = line[1..^1].Trim();
            if (!result.ContainsKey(section))
                result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            result[section][line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
    }
    return result;
}

// ── Shared setup-save logic ───────────────────────────────────────────────────
static async Task<IResult> ExecuteSetupSave(
    AgentConfigService cfgSvc,
    ILogger logger,
    string? carId, string? trackId, string? fileName, string? setupText,
    bool overwrite, string? relPath, bool versioned = false)
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

        // When versioned, generate an AI-stamped filename and never overwrite the original.
        if (versioned)
        {
            var baseName  = Path.GetFileNameWithoutExtension(safeFileName);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            safeFileName  = $"{baseName}__AI_{timestamp}.ini";
            overwrite     = false;
        }

        var dir = Path.Combine(root, safeCarId, safeTrackId);
        savedPath = Path.Combine(dir, safeFileName);
        if (!Path.GetFullPath(savedPath).StartsWith(
                Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Resolved path escapes setup directory." });
    }

    logger.LogInformation(
        "SAVE REQ car={Car} track={Track} file={File} versioned={Versioned} bytes={Bytes}",
        carId, trackId, safeFileName, versioned, setupText.Length);

    if (!overwrite && File.Exists(savedPath))
        return Results.Conflict(new { error = "File already exists. Set overwrite=true to replace." });

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(savedPath)!);
        var tmp = savedPath + ".tmp";
        await File.WriteAllTextAsync(tmp, setupText);
        File.Move(tmp, savedPath, overwrite: true);
        logger.LogInformation("SAVE OK path={Path}", savedPath);
        return Results.Ok(new { ok = true, savedFileName = safeFileName, fullPath = savedPath });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "SAVE ERR");
        return Results.Problem(ex.Message);
    }
}

// ── Request models ────────────────────────────────────────────────────────────
record ApplySetupChangeDto(string Section, string Key, string Value);

record ApplySetupRequestDto(
    string?                    Car,
    string?                    Track,
    string?                    BaseFile,
    List<ApplySetupChangeDto>? Changes,
    bool                       CreateVersionedCopy = false,
    string?                    Reason              = null);

record SetupSaveMetadata(
    string                    BaseFile,
    string                    SavedFile,
    string                    CreatedUtc,
    string                    Car,
    string                    Track,
    List<ApplySetupChangeDto> Changes,
    string?                   Reason,
    string                    Client,
    string                    AgentVersion);

record SetupApplyRequest(
    string? Car,
    string? Track,
    string? File,
    string? IniContent,
    bool    Versioned = false,
    string? Tag       = "AI");

record SetupSaveRequest(
    string? CarId,
    string? TrackId,
    string? FileName,
    string? SetupText,
    bool    Overwrite = true,
    bool    Versioned = false);

record ReferenceRootSetRequest(string? Path);

record SetupVersionedSaveRequest(
    string? Car,
    string? Track,
    string? BaseFileName,
    string? Content);

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

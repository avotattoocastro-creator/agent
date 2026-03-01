using System.Text.Json;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Loads configuration from appsettings (via IConfiguration) and applies
/// overrides from a writable JSON file in Documents/AvoTelemetryAgent/.
/// Provides a thread-safe Current property and a SaveAsync method.
/// </summary>
public sealed class AgentConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = true,
        PropertyNameCaseInsensitive = true,
    };

    private volatile AgentConfig _current;

    public AgentConfig Current => _current;
    public string OverrideFilePath { get; }

    public AgentConfigService(IConfiguration configuration)
    {
        // Base config from appsettings.
        var base_ = new AgentConfig();
        configuration.GetSection("AvoAgent").Bind(base_);
        _current = base_;

        // Override file location.
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        OverrideFilePath = Path.Combine(docs, "AvoTelemetryAgent", "agent.config.json");

        ApplyOverrides();
    }

    public async Task SaveAsync(AgentConfig config)
    {
        _current = config;
        var dir = Path.GetDirectoryName(OverrideFilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(config, JsonOpts);
        var tmp  = OverrideFilePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, OverrideFilePath, overwrite: true);
        }
        catch
        {
            // Clean up the temp file on failure to avoid stale files.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void ApplyOverrides()
    {
        if (!File.Exists(OverrideFilePath)) return;
        try
        {
            var json = File.ReadAllText(OverrideFilePath);
            var ov   = JsonSerializer.Deserialize<AgentConfig>(json, JsonOpts);
            if (ov is not null) _current = ov;
        }
        catch { /* ignore malformed override file */ }
    }
}

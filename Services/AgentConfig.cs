namespace AvoTelemetryAgent.Services;

/// <summary>Full runtime configuration. Persisted to Documents/AvoTelemetryAgent/agent.config.json.</summary>
public sealed class AgentConfig
{
    public int    Port       { get; set; } = 8181;
    public string Token      { get; set; } = "change-me";
    public int    PhysicsHz  { get; set; } = 60;
    public int    GraphicsHz { get; set; } = 20;
    public int    StaticHz   { get; set; } = 1;

    public SetupSection     Setup     { get; set; } = new();
    public DiscoverySection Discovery { get; set; } = new();
    public AdminUiSection   AdminUi   { get; set; } = new();
    public AgentSection     Agent     { get; set; } = new();
}

public sealed class SetupSection
{
    public string DefaultRoot { get; set; } = string.Empty;
}

public sealed class DiscoverySection
{
    public bool Enabled    { get; set; } = true;
    public int  Port       { get; set; } = 8182;
    public int  IntervalMs { get; set; } = 2000;
}

public sealed class AdminUiSection
{
    public bool BindLocalhostOnly { get; set; } = true;
    public bool AllowRemote       { get; set; } = false;
}

public sealed class AgentSection
{
    public bool AllowAutostartOperations { get; set; } = true;
    public bool AutoStartStreaming       { get; set; } = true;
    public bool AutoStopWhenAcCloses     { get; set; } = false;
}

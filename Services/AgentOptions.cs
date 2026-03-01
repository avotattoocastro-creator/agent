namespace AvoTelemetryAgent.Services;

public sealed class AgentOptions
{
    /// <summary>Bearer token required for WebSocket and setup endpoints.</summary>
    public string Token { get; set; } = "change-me";

    /// <summary>HTTP port the agent listens on.</summary>
    public int HttpPort { get; set; } = 8181;

    /// <summary>UDP port used for LAN discovery broadcasts.</summary>
    public int DiscoveryPort { get; set; } = 8182;

    /// <summary>Physics polling rate in Hz (default 60).</summary>
    public int PhysicsHz { get; set; } = 60;

    /// <summary>Graphics polling rate in Hz (default 20).</summary>
    public int GraphicsHz { get; set; } = 20;

    /// <summary>How often to re-read static data in milliseconds (default 2000).</summary>
    public int StaticIntervalMs { get; set; } = 2000;
}

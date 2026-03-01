using AvoTelemetryAgent.SharedMemory;

namespace AvoTelemetryAgent.Models;

/// <summary>
/// A unified, timestamped snapshot of all three AC shared-memory pages.
/// </summary>
public sealed class AcTelemetrySnapshot
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public SPageFilePhysics  Physics  { get; init; }
    public SPageFileGraphics Graphics { get; init; }
    public SPageFileStatic   Static   { get; init; }

    public bool IsConnected { get; init; }
}

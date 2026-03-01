namespace AvoTelemetryAgent.Models;

// ── DTOs streamed as JSON over WebSocket ────────────────────────────────────

public sealed class TelemetryFrame
{
    public string   TUtc    { get; set; } = string.Empty;
    public long     Seq     { get; set; }
    public string   CarId   { get; set; } = string.Empty;
    public string   TrackId { get; set; } = string.Empty;

    public PhysicsDto  Physics  { get; set; } = new();
    public GraphicsDto Graphics { get; set; } = new();
    public StaticsDto  Statics  { get; set; } = new();
    public StatusDto   Status   { get; set; } = new();
}

public sealed class PhysicsDto
{
    public float   SpeedKmh    { get; set; }
    public int     Rpm         { get; set; }
    public int     Gear        { get; set; }
    public float   Throttle    { get; set; }
    public float   Brake       { get; set; }
    public float   Steer       { get; set; }
    public float   Clutch      { get; set; }
    public float   LatG        { get; set; }
    public float   LongG       { get; set; }
    public float   YawRate     { get; set; }
    public float[] WheelSlip   { get; set; } = new float[4];
    public float[] TyreTemp    { get; set; } = new float[4];
    public float[] TyrePressure { get; set; } = new float[4];
}

public sealed class GraphicsDto
{
    public string Session       { get; set; } = string.Empty;
    public string AcStatus      { get; set; } = string.Empty;
    public int    Lap           { get; set; }
    public int    LapTimeMs     { get; set; }
    public int    BestLapMs     { get; set; }
    public int    Position      { get; set; }
    public int    CompletedLaps { get; set; }
    public int    CurrentTimeMs { get; set; }
}

public sealed class StaticsDto
{
    public string CarModel  { get; set; } = string.Empty;
    public string Track     { get; set; } = string.Empty;
    public float  MaxRpm    { get; set; }
    public float  MaxFuel   { get; set; }
}

public sealed class StatusDto
{
    public bool   Connected { get; set; }
    public string Message   { get; set; } = string.Empty;
}

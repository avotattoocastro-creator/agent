namespace AvoTelemetryAgent.Models;

/// <summary>A single setup file found under the Reference Setups root.</summary>
public sealed class SetupRefItem
{
    public string   CarId        { get; set; } = string.Empty;
    public string   TrackId      { get; set; } = string.Empty;
    public string   FileName     { get; set; } = string.Empty;
    public string   DisplayName  { get; set; } = string.Empty;
    public string   RelativePath { get; set; } = string.Empty;
    public DateTime UpdatedUtc   { get; set; }
    public long     SizeBytes    { get; set; }
}

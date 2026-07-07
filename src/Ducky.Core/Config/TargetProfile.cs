namespace Ducky.Core.Config;

public sealed class TargetProfile
{
    public string Id { get; set; } = "beeper";
    public string DisplayName { get; set; } = "Beeper";
    public string TargetProcess { get; set; } = "beeper";
    public int DurationThresholdMs { get; set; } = 452;
    public float PeakThreshold { get; set; } = 0.01f;
    public bool Enabled { get; set; } = true;

    public override string ToString() => $"{DisplayName} ({TargetProcess})";
}

namespace Ducky.Core.Config;

public sealed class CalibrationData
{
    public string TargetProcess { get; set; } = "beeper";
    public float PeakThreshold { get; set; } = 0.01f;
    public int HeadroomMs { get; set; } = 150;
    public int SuggestedThresholdMs { get; set; }
    public int MaxDurationMs { get; set; }
    public int MinDurationMs { get; set; }
    public double AverageDurationMs { get; set; }
    public double P95DurationMs { get; set; }
    public int EventCount { get; set; }
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public List<CalibrationEventRecord> Events { get; set; } = new();
}

public sealed class CalibrationEventRecord
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public double DurationMs { get; set; }
    public float PeakMax { get; set; }
}

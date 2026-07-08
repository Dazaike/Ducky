namespace Ducky.Core.Config;

public sealed class DuckingSettings
{
    public string TargetProcess { get; set; } = "beeper";
    public int DurationThresholdMs { get; set; } = 1000;
    public float PeakThreshold { get; set; } = 0.01f;
    public int HangTimeMs { get; set; } = 300;
    public string DuckingMode { get; set; } = "Mute";
    public float DuckRatio { get; set; } = 0.15f;
    public int DuckFadeMs { get; set; } = 300;
    public List<string> ExcludedProcesses { get; set; } = new();
    public bool Enabled { get; set; } = true;

    /// <summary>Only duck when audio is actively playing on the background device.</summary>
    public bool RequireBackgroundAudio { get; set; } = true;

    /// <summary>Comma-separated substrings matched against device friendly names. Empty uses the default output device.</summary>
    public string BackgroundAudioDevicePattern { get; set; } = string.Empty;

    /// <summary>Only mute sessions that are currently outputting audio above PeakThreshold.</summary>
    public bool OnlyDuckActiveSessions { get; set; } = true;

    public int IdlePollIntervalMs { get; set; } = 300;
    public int ActivePollIntervalMs { get; set; } = 30;
}

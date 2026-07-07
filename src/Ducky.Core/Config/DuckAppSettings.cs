namespace Ducky.Core.Config;

using Ducky.Core;

public sealed class DuckAppSettings
{
    public bool Enabled { get; set; } = true;
    public List<TargetProfile> Profiles { get; set; } = CreateDefaultProfiles();

    public int HangTimeMs { get; set; } = 300;
    public string DuckingMode { get; set; } = "Mute";
    public float DuckRatio { get; set; } = 0.15f;
    public int DuckFadeMs { get; set; } = 300;
    public List<string> ExcludedProcesses { get; set; } = new();
    public bool RequireBackgroundAudio { get; set; } = true;
    public string BackgroundAudioDevicePattern { get; set; } = string.Empty;
    public bool OnlyDuckActiveSessions { get; set; } = true;
    public int IdlePollIntervalMs { get; set; } = 300;
    public int ActivePollIntervalMs { get; set; } = 30;
    public bool TraceEnabled { get; set; }

    public static List<TargetProfile> CreateDefaultProfiles() =>
    [
        new TargetProfile
        {
            Id = "beeper",
            DisplayName = "Beeper",
            TargetProcess = "beeper",
            DurationThresholdMs = 452,
            Enabled = true
        },
        new TargetProfile
        {
            Id = "discord",
            DisplayName = "Discord",
            TargetProcess = "Discord",
            DurationThresholdMs = 1000,
            Enabled = true
        }
    ];

    public DuckingSettings ToEngineSettings(TargetProfile profile) =>
        new()
        {
            TargetProcess = profile.TargetProcess,
            DurationThresholdMs = profile.DurationThresholdMs,
            PeakThreshold = profile.PeakThreshold,
            Enabled = Enabled && profile.Enabled,
            HangTimeMs = HangTimeMs,
            DuckingMode = DuckingMode,
            DuckRatio = DuckRatio,
            DuckFadeMs = DuckFadeMs,
            ExcludedProcesses = ExcludedProcesses.ToList(),
            RequireBackgroundAudio = RequireBackgroundAudio,
            BackgroundAudioDevicePattern = BackgroundAudioDevicePattern,
            OnlyDuckActiveSessions = OnlyDuckActiveSessions,
            IdlePollIntervalMs = IdlePollIntervalMs,
            ActivePollIntervalMs = ActivePollIntervalMs
        };

    public void ApplyCalibration(TargetProfile profile, CalibrationData calibration)
    {
        profile.TargetProcess = calibration.TargetProcess;
        profile.PeakThreshold = calibration.PeakThreshold;

        if (calibration.Events.Count > 0)
        {
            var durations = calibration.Events.Select(e => e.DurationMs).ToList();
            profile.DurationThresholdMs = CalibrationStats.Compute(durations, calibration.HeadroomMs).SuggestedThresholdMs;
        }
        else
        {
            profile.DurationThresholdMs = calibration.SuggestedThresholdMs;
        }
    }

    public void LoadCalibrationsFromDisk()
    {
        foreach (var profile in Profiles)
        {
            var calibration = SettingsStore.LoadCalibration(AppPaths.CalibrationPathForProfile(profile.Id));
            if (calibration is not null)
            {
                ApplyCalibration(profile, calibration);
            }
        }
    }
}

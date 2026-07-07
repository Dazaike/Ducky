using System.Text.Json;
using System.Text.Json.Nodes;
using Ducky.Core.Config;

namespace Ducky.Core;

public static class AppPaths
{
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppBranding.AppDataFolder);

    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    public static string CalibrationDirectory => Path.Combine(AppDataDirectory, "calibration");

    public static string DefaultCalibrationPath => Path.Combine(AppDataDirectory, "calibration.json");

    public static string CalibrationPathForProfile(string profileId) =>
        Path.Combine(CalibrationDirectory, $"{profileId}.json");

    public static void EnsureAppDataMigrated()
    {
        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppBranding.LegacyAppDataFolder);

        if (!Directory.Exists(legacyDir) || Directory.Exists(AppDataDirectory))
        {
            return;
        }

        try
        {
            Directory.Move(legacyDir, AppDataDirectory);
        }
        catch
        {
            // Fall back to fresh app data if migration fails.
        }
    }
}

public static class SettingsStore
{
    public static DuckAppSettings LoadAppSettings()
    {
        AppPaths.EnsureAppDataMigrated();

        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
            {
                var defaults = new DuckAppSettings();
                MigrateLegacyCalibration(defaults);
                return defaults;
            }

            var json = File.ReadAllText(AppPaths.SettingsPath);
            var node = JsonNode.Parse(json);
            if (node is null)
            {
                return new DuckAppSettings();
            }

            if (node["profiles"] is null)
            {
                return MigrateLegacySettings(json);
            }

            var settings = JsonSerializer.Deserialize(json, DuckAppJsonContext.Default.DuckAppSettings) ?? new DuckAppSettings();
            settings.LoadCalibrationsFromDisk();
            return settings;
        }
        catch
        {
            return new DuckAppSettings();
        }
    }

    public static void SaveAppSettings(DuckAppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var json = JsonSerializer.Serialize(settings, DuckAppJsonContext.Default.DuckAppSettings);
        File.WriteAllText(AppPaths.SettingsPath, json);
    }

    [Obsolete("Use LoadAppSettings")]
    public static DuckingSettings LoadSettings()
    {
        var app = LoadAppSettings();
        var profile = app.Profiles.FirstOrDefault() ?? DuckAppSettings.CreateDefaultProfiles()[0];
        return app.ToEngineSettings(profile);
    }

    [Obsolete("Use SaveAppSettings")]
    public static void SaveSettings(DuckingSettings settings)
    {
        var app = LoadAppSettings();
        var profile = app.Profiles.FirstOrDefault();
        if (profile is null)
        {
            profile = new TargetProfile();
            app.Profiles.Add(profile);
        }

        profile.TargetProcess = settings.TargetProcess;
        profile.DurationThresholdMs = settings.DurationThresholdMs;
        profile.PeakThreshold = settings.PeakThreshold;
        profile.Enabled = settings.Enabled;
        app.Enabled = settings.Enabled;
        app.HangTimeMs = settings.HangTimeMs;
        app.DuckingMode = settings.DuckingMode;
        app.DuckRatio = settings.DuckRatio;
        app.ExcludedProcesses = settings.ExcludedProcesses;
        app.RequireBackgroundAudio = settings.RequireBackgroundAudio;
        app.BackgroundAudioDevicePattern = settings.BackgroundAudioDevicePattern;
        app.OnlyDuckActiveSessions = settings.OnlyDuckActiveSessions;
        app.IdlePollIntervalMs = settings.IdlePollIntervalMs;
        app.ActivePollIntervalMs = settings.ActivePollIntervalMs;
        SaveAppSettings(app);
    }

    public static CalibrationData? LoadCalibration(string? path = null)
    {
        path ??= AppPaths.DefaultCalibrationPath;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, DuckAppJsonContext.Default.CalibrationData);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveCalibration(CalibrationData data, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        data.ExportedAtUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(data, DuckAppJsonContext.Default.CalibrationData);
        File.WriteAllText(path, json);
    }

    public static void SaveCalibrationForProfile(CalibrationData data, string profileId)
    {
        SaveCalibration(data, AppPaths.CalibrationPathForProfile(profileId));
    }

    private static DuckAppSettings MigrateLegacySettings(string json)
    {
        var legacy = JsonSerializer.Deserialize(json, DuckAppJsonContext.Default.DuckingSettings) ?? new DuckingSettings();
        var app = new DuckAppSettings
        {
            Enabled = legacy.Enabled,
            HangTimeMs = legacy.HangTimeMs,
            DuckingMode = legacy.DuckingMode,
            DuckRatio = legacy.DuckRatio,
            ExcludedProcesses = legacy.ExcludedProcesses,
            RequireBackgroundAudio = legacy.RequireBackgroundAudio,
            BackgroundAudioDevicePattern = legacy.BackgroundAudioDevicePattern,
            OnlyDuckActiveSessions = legacy.OnlyDuckActiveSessions,
            IdlePollIntervalMs = legacy.IdlePollIntervalMs,
            ActivePollIntervalMs = legacy.ActivePollIntervalMs,
            Profiles =
            [
                new TargetProfile
                {
                    Id = "beeper",
                    DisplayName = "Beeper",
                    TargetProcess = legacy.TargetProcess,
                    DurationThresholdMs = legacy.DurationThresholdMs,
                    PeakThreshold = legacy.PeakThreshold,
                    Enabled = true
                }
            ]
        };

        MigrateLegacyCalibration(app);
        return app;
    }

    private static void MigrateLegacyCalibration(DuckAppSettings app)
    {
        if (!File.Exists(AppPaths.DefaultCalibrationPath))
        {
            app.LoadCalibrationsFromDisk();
            return;
        }

        var calibration = LoadCalibration(AppPaths.DefaultCalibrationPath);
        if (calibration is null)
        {
            return;
        }

        var beeper = app.Profiles.FirstOrDefault(p => p.Id == "beeper");
        if (beeper is not null)
        {
            app.ApplyCalibration(beeper, calibration);
            SaveCalibrationForProfile(calibration, "beeper");
        }
    }
}

public static class CalibrationStats
{
    public sealed class Summary
    {
        public int Count { get; init; }
        public int MinMs { get; init; }
        public int MaxMs { get; init; }
        public double AverageMs { get; init; }
        public double P95Ms { get; init; }
        public int SuggestedThresholdMs { get; init; }
    }

    public static Summary Compute(IReadOnlyList<double> durationsMs, int headroomMs)
    {
        if (durationsMs.Count == 0)
        {
            return new Summary();
        }

        var ordered = durationsMs.OrderBy(d => d).ToList();
        var min = (int)Math.Round(ordered[0]);
        var max = (int)Math.Round(ordered[^1]);
        var avg = ordered.Average();
        var p95Index = Math.Min(ordered.Count - 1, (int)Math.Ceiling(ordered.Count * 0.95) - 1);
        var p95 = ordered[Math.Max(0, p95Index)];

        var shortEvents = ordered.Where(d => d < 1000).ToList();
        var notificationMaxMs = shortEvents.Count > 0
            ? (int)Math.Round(shortEvents[^1])
            : min;

        return new Summary
        {
            Count = ordered.Count,
            MinMs = min,
            MaxMs = max,
            AverageMs = avg,
            P95Ms = p95,
            SuggestedThresholdMs = notificationMaxMs + headroomMs
        };
    }
}

using NAudio.CoreAudioApi;
using Ducky.Core.Config;

namespace Ducky.Core.Audio;

public sealed class SavedSessionState
{
    public string ProcessName { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public uint ProcessId { get; init; }
    public bool WasMuted { get; init; }
    public float Volume { get; init; }
    public float DuckVolume { get; init; }
}

public sealed class DuckingController
{
    private readonly object _lock = new();
    private readonly List<SavedSessionState> _savedStates = new();
    private readonly VolumeFader _fader = new();
    private bool _isDucked;
    private bool _restoreInProgress;
    private DuckingSettings? _activeSettings;

    public bool IsDucked => _isDucked;

    public void ForceRestore(DuckingSettings settings, uint selfPid)
    {
        _fader.Cancel();

        List<SavedSessionState> toRestore;
        lock (_lock)
        {
            toRestore = _savedStates.ToList();
            _savedStates.Clear();
            _isDucked = false;
            _restoreInProgress = false;
            _activeSettings = null;
        }

        RestoreInstant(toRestore);
        EmergencyUnmuteAll(settings.TargetProcess, selfPid, settings);
    }

    public void EmergencyUnmuteAll(string targetProcess, uint selfPid, DuckingSettings settings)
    {
        _fader.Cancel();

        lock (_lock)
        {
            _savedStates.Clear();
            _isDucked = false;
            _restoreInProgress = false;
            _activeSettings = null;
        }

        var targetName = NormalizeProcessName(targetProcess);
        var excluded = new HashSet<string>(
            settings.ExcludedProcesses.Select(NormalizeProcessName),
            StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<AudioSessionInfo> sessions;
        try
        {
            sessions = SessionEnumerator.GetRenderSessions();
        }
        catch
        {
            return;
        }

        foreach (var info in sessions)
        {
            if (ShouldSkipSession(info, targetName, selfPid, excluded))
            {
                continue;
            }

            try
            {
                info.Session.SimpleAudioVolume.Mute = false;
            }
            catch
            {
                // Session may have ended.
            }
        }
    }

    public void ApplyDucking(string targetProcess, uint selfPid, DuckingSettings settings)
    {
        lock (_lock)
        {
            if (_isDucked && !_restoreInProgress)
            {
                return;
            }

            _fader.Cancel();
            _restoreInProgress = false;
            _savedStates.Clear();
            _activeSettings = settings;
        }

        var isDuckMode = settings.DuckingMode.Equals("Duck", StringComparison.OrdinalIgnoreCase);
        var fadeMs = isDuckMode ? settings.DuckFadeMs : 0;
        var targetName = NormalizeProcessName(targetProcess);
        var excluded = new HashSet<string>(
            settings.ExcludedProcesses.Select(NormalizeProcessName),
            StringComparer.OrdinalIgnoreCase);

        var candidates = settings.RequireBackgroundAudio
            ? SessionEnumerator.GetSessionsOnDevice(settings.BackgroundAudioDevicePattern)
            : SessionEnumerator.GetRenderSessions();

        var savedStates = new List<SavedSessionState>();
        var fadeTargets = new List<VolumeFadeTarget>();

        foreach (var info in candidates)
        {
            if (ShouldSkipSession(info, targetName, selfPid, excluded))
            {
                continue;
            }

            if (settings.OnlyDuckActiveSessions &&
                !SessionEnumerator.ShouldDuckSession(info, settings.PeakThreshold))
            {
                continue;
            }

            try
            {
                var volume = info.Session.SimpleAudioVolume;
                var wasMuted = volume.Mute;
                var currentVolume = volume.Volume;

                if (isDuckMode)
                {
                    var duckVolume = Math.Clamp(currentVolume * settings.DuckRatio, 0f, 1f);
                    savedStates.Add(new SavedSessionState
                    {
                        ProcessName = info.ProcessName,
                        SessionId = info.SessionId,
                        DeviceId = info.DeviceId,
                        ProcessId = info.ProcessId,
                        WasMuted = wasMuted,
                        Volume = currentVolume,
                        DuckVolume = duckVolume
                    });

                    fadeTargets.Add(CreateFadeTarget(volume, currentVolume, duckVolume, wasMuted));
                }
                else
                {
                    savedStates.Add(new SavedSessionState
                    {
                        ProcessName = info.ProcessName,
                        SessionId = info.SessionId,
                        DeviceId = info.DeviceId,
                        ProcessId = info.ProcessId,
                        WasMuted = wasMuted,
                        Volume = currentVolume,
                        DuckVolume = currentVolume
                    });

                    volume.Mute = true;
                }
            }
            catch
            {
                // Session may have ended before we could duck it.
            }
        }

        lock (_lock)
        {
            if (savedStates.Count == 0)
            {
                _isDucked = false;
                return;
            }

            _savedStates.Clear();
            _savedStates.AddRange(savedStates);
            _isDucked = true;
        }

        if (isDuckMode)
        {
            _fader.Fade(fadeTargets, fadeMs);
        }
    }

    public void RestoreAll()
    {
        List<SavedSessionState> toRestore;
        DuckingSettings? settings;

        lock (_lock)
        {
            if (!_isDucked)
            {
                return;
            }

            toRestore = _savedStates.ToList();
            settings = _activeSettings;
            _restoreInProgress = true;
        }

        var isDuckMode = settings?.DuckingMode.Equals("Duck", StringComparison.OrdinalIgnoreCase) == true;
        var fadeMs = isDuckMode ? settings!.DuckFadeMs : 0;

        if (!isDuckMode || fadeMs <= 0)
        {
            _fader.Cancel();
            RestoreInstant(toRestore);
            FinishRestore();
            return;
        }

        var sessions = SessionEnumerator.GetRenderSessions();
        var fadeTargets = new List<VolumeFadeTarget>();

        foreach (var saved in toRestore)
        {
            var match = FindBestMatch(sessions, saved) ?? FindLooseMatch(sessions, saved);
            if (match is null)
            {
                continue;
            }

            try
            {
                var volume = match.Session.SimpleAudioVolume;
                fadeTargets.Add(CreateFadeTarget(volume, volume.Volume, saved.Volume, saved.WasMuted));
            }
            catch
            {
                // Session may have ended.
            }
        }

        _fader.Fade(fadeTargets, fadeMs, FinishRestore);
    }

    private void FinishRestore()
    {
        lock (_lock)
        {
            _savedStates.Clear();
            _isDucked = false;
            _restoreInProgress = false;
            _activeSettings = null;
        }
    }

    private void RestoreInstant(IReadOnlyList<SavedSessionState> toRestore)
    {
        var sessions = SessionEnumerator.GetRenderSessions();
        foreach (var saved in toRestore)
        {
            var match = FindBestMatch(sessions, saved) ?? FindLooseMatch(sessions, saved);
            if (match is null)
            {
                continue;
            }

            try
            {
                var volume = match.Session.SimpleAudioVolume;
                volume.Volume = saved.Volume;
                volume.Mute = saved.WasMuted;
            }
            catch
            {
                // Best-effort restore if the session no longer exists.
            }
        }
    }

    private static VolumeFadeTarget CreateFadeTarget(
        SimpleAudioVolume volume,
        float from,
        float to,
        bool restoreMute) =>
        new()
        {
            From = from,
            To = to,
            RestoreMute = restoreMute,
            GetVolume = () => volume.Volume,
            SetVolume = value => volume.Volume = value,
            SetMute = muted => volume.Mute = muted
        };

    private static AudioSessionInfo? FindLooseMatch(
        IReadOnlyList<AudioSessionInfo> sessions,
        SavedSessionState saved)
    {
        foreach (var session in sessions)
        {
            if (session.ProcessId == saved.ProcessId)
            {
                return session;
            }
        }

        foreach (var session in sessions)
        {
            if (!string.IsNullOrEmpty(saved.ProcessName) &&
                session.ProcessName.Equals(saved.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }
        }

        return null;
    }

    private static bool ShouldSkipSession(
        AudioSessionInfo info,
        string targetProcessName,
        uint selfPid,
        HashSet<string> excluded)
    {
        if (info.IsSystemSounds)
        {
            return true;
        }

        if (info.ProcessId == selfPid)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(info.ProcessName) &&
            info.ProcessName.Equals(targetProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (info.ProcessId == 0)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(info.ProcessName) && excluded.Contains(info.ProcessName))
        {
            return true;
        }

        if (info.ProcessName.Equals("audiodg", StringComparison.OrdinalIgnoreCase) ||
            info.ProcessName.Equals("SteelSeriesSonar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static AudioSessionInfo? FindBestMatch(IReadOnlyList<AudioSessionInfo> sessions, SavedSessionState saved)
    {
        AudioSessionInfo? match = null;

        foreach (var session in sessions)
        {
            if (!string.IsNullOrEmpty(saved.SessionId) &&
                string.Equals(session.SessionId, saved.SessionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(session.DeviceId, saved.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }

            if (session.ProcessId == saved.ProcessId &&
                string.Equals(session.DeviceId, saved.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                match ??= session;
            }
            else if (match is null &&
                     string.Equals(session.ProcessName, saved.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(session.DeviceId, saved.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                match = session;
            }
        }

        return match;
    }

    private static string NormalizeProcessName(string name)
    {
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

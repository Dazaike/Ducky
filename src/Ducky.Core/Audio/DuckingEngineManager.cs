using Ducky.Core;
using Ducky.Core.Config;

namespace Ducky.Core.Audio;

public sealed class DuckingEngineManager : IDisposable
{
    private readonly DuckAppSettings _settings;
    private readonly SharedDuckingCoordinator _coordinator = new();
    private readonly List<DuckingEngine> _engines = new();
    private readonly uint _selfPid = (uint)Environment.ProcessId;
    private System.Threading.Timer? _diagTimer;
    private bool _disposed;
    private bool _started;

    public DuckingEngineManager(DuckAppSettings settings)
    {
        _settings = settings;
        RebuildEngines();
    }

    public bool IsEnabled
    {
        get => _settings.Enabled;
        set
        {
            _settings.Enabled = value;
            foreach (var engine in _engines)
            {
                engine.IsEnabled = value && engine.Profile.Enabled;
            }
        }
    }

    public void SetTraceEnabled(bool enabled)
    {
        DuckTrace.SetEnabled(enabled);
        if (enabled)
        {
            _diagTimer ??= new System.Threading.Timer(DiagnosticHeartbeat, null, 0, 5000);
            _diagTimer.Change(0, 5000);
            foreach (var engine in _engines)
            {
                engine.WriteDiagnosticSnapshot();
            }
        }
        else
        {
            _diagTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void DiagnosticHeartbeat(object? _)
    {
        foreach (var engine in _engines)
        {
            engine.WriteDiagnosticSnapshot();
        }
    }

    public void WriteDiagnosticSnapshots()
    {
        foreach (var engine in _engines)
        {
            engine.WriteDiagnosticSnapshot();
        }
    }

    public event Action? StateChanged;

    public IReadOnlyList<(TargetProfile Profile, DuckingState State)> GetStates()
    {
        return _engines.Select(e => (e.Profile, e.State)).ToList();
    }

    public void UpdateSettings(DuckAppSettings settings)
    {
        _settings.Enabled = settings.Enabled;
        _settings.Profiles = settings.Profiles.Select(CloneProfile).ToList();
        _settings.HangTimeMs = settings.HangTimeMs;
        _settings.DuckingMode = settings.DuckingMode;
        _settings.DuckRatio = settings.DuckRatio;
        _settings.DuckFadeMs = settings.DuckFadeMs;
        _settings.ExcludedProcesses = settings.ExcludedProcesses.ToList();
        _settings.RequireBackgroundAudio = settings.RequireBackgroundAudio;
        _settings.BackgroundAudioDevicePattern = settings.BackgroundAudioDevicePattern;
        _settings.OnlyDuckActiveSessions = settings.OnlyDuckActiveSessions;
        _settings.IdlePollIntervalMs = settings.IdlePollIntervalMs;
        _settings.ActivePollIntervalMs = settings.ActivePollIntervalMs;
        _settings.TraceEnabled = settings.TraceEnabled;
        RebuildEngines();
    }

    public void ForceRestoreAll()
    {
        foreach (var engine in _engines)
        {
            engine.ForceRestore();
        }

        var sampleProfile = _settings.Profiles.FirstOrDefault();
        if (sampleProfile is not null)
        {
            _coordinator.ForceRestoreAll(_settings.ToEngineSettings(sampleProfile), _selfPid);
        }
    }

    public void Start()
    {
        foreach (var engine in _engines)
        {
            engine.Start();
        }

        _started = true;
    }

    public void Stop()
    {
        ForceRestoreAll();
        foreach (var engine in _engines)
        {
            engine.Stop();
        }

        _started = false;
    }

    private void RebuildEngines()
    {
        var wasStarted = _started;

        foreach (var engine in _engines)
        {
            engine.Dispose();
        }

        _engines.Clear();
        ForceRestoreAll();

        foreach (var profile in _settings.Profiles.Where(p => p.Enabled))
        {
            var engine = new DuckingEngine(_settings.ToEngineSettings(profile), _coordinator, profile);
            engine.StateChanged += _ => StateChanged?.Invoke();
            _engines.Add(engine);
        }

        if (wasStarted)
        {
            foreach (var engine in _engines)
            {
                engine.Start();
            }
        }
    }

    private static TargetProfile CloneProfile(TargetProfile profile) =>
        new()
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            TargetProcess = profile.TargetProcess,
            DurationThresholdMs = profile.DurationThresholdMs,
            PeakThreshold = profile.PeakThreshold,
            Enabled = profile.Enabled
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _diagTimer?.Dispose();
        Stop();
        foreach (var engine in _engines)
        {
            engine.Dispose();
        }

        _engines.Clear();
    }
}

using Ducky.Core;
using Ducky.Core.Config;

namespace Ducky.Core.Audio;

public enum DuckingState
{
    Idle,
    Pending,
    Ducking
}

public sealed class DuckingEngine : IDisposable
{
    private readonly DuckingSettings _settings;
    private readonly SharedDuckingCoordinator _coordinator;
    private readonly PeakMonitor _peakMonitor;
    private readonly System.Threading.Timer _stateTimer;
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private readonly object _stateLock = new();
    private DuckingState _state = DuckingState.Idle;
    private DateTime? _pendingStartUtc;
    private DateTime? _hangStartUtc;
    private bool _disposed;
    private bool _acquired;
    private bool _loggedPendingBlock;

    public DuckingEngine(DuckingSettings settings, SharedDuckingCoordinator coordinator, TargetProfile profile)
    {
        Profile = profile;
        _settings = settings;
        _coordinator = coordinator;
        _peakMonitor = new PeakMonitor(
            settings.TargetProcess,
            settings.PeakThreshold,
            pollIntervalMs: settings.ActivePollIntervalMs,
            startConsecutivePolls: 2,
            endDebounceMs: 100);
        _peakMonitor.SetPollIntervals(settings.IdlePollIntervalMs, settings.ActivePollIntervalMs);
        _peakMonitor.PlaybackStarted += OnPlaybackStarted;
        _peakMonitor.PlaybackCompleted += OnPlaybackCompleted;
        _stateTimer = new System.Threading.Timer(EvaluateState, null, Timeout.Infinite, Timeout.Infinite);
    }

    public TargetProfile Profile { get; }

    public DuckingState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsEnabled { get; set; } = true;

    public event Action<DuckingState>? StateChanged;

    public void UpdateSettings(DuckingSettings settings)
    {
        var oldPattern = _settings.BackgroundAudioDevicePattern;
        _settings.TargetProcess = settings.TargetProcess;
        _settings.DurationThresholdMs = settings.DurationThresholdMs;
        _settings.PeakThreshold = settings.PeakThreshold;
        _settings.HangTimeMs = settings.HangTimeMs;
        _settings.DuckingMode = settings.DuckingMode;
        _settings.DuckRatio = settings.DuckRatio;
        _settings.DuckFadeMs = settings.DuckFadeMs;
        _settings.ExcludedProcesses = settings.ExcludedProcesses.ToList();
        _settings.RequireBackgroundAudio = settings.RequireBackgroundAudio;
        _settings.BackgroundAudioDevicePattern = settings.BackgroundAudioDevicePattern;
        if (!string.Equals(
                oldPattern,
                settings.BackgroundAudioDevicePattern,
                StringComparison.OrdinalIgnoreCase))
        {
            SessionEnumerator.InvalidateDeviceCache();
        }
        _settings.OnlyDuckActiveSessions = settings.OnlyDuckActiveSessions;
        _settings.IdlePollIntervalMs = settings.IdlePollIntervalMs;
        _settings.ActivePollIntervalMs = settings.ActivePollIntervalMs;
        _peakMonitor.TargetProcess = settings.TargetProcess;
        _peakMonitor.PeakThreshold = settings.PeakThreshold;
        _peakMonitor.SetPollIntervals(settings.IdlePollIntervalMs, settings.ActivePollIntervalMs);
        IsEnabled = settings.Enabled;
    }

    public void ForceRestore()
    {
        lock (_stateLock)
        {
            ReleaseBackgroundAudio();
            _pendingStartUtc = null;
            _hangStartUtc = null;
            SetState(DuckingState.Idle);
        }
    }

    public void Start()
    {
        _peakMonitor.Start();
        UpdateStateTimerInterval();
    }

    public void Stop()
    {
        _peakMonitor.Stop();
        ForceRestore();
        _stateTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnPlaybackStarted()
    {
        lock (_stateLock)
        {
            if (_state == DuckingState.Ducking)
            {
                return;
            }

            _peakMonitor.SetFastPolling(true);
            _hangStartUtc = null;
            _loggedPendingBlock = false;
            _pendingStartUtc = _peakMonitor.CurrentEventStartUtc ?? DateTime.UtcNow;
            SetState(DuckingState.Pending);
        }
    }

    private void OnPlaybackCompleted(PlaybackEvent _)
    {
        lock (_stateLock)
        {
            if (_state == DuckingState.Pending)
            {
                _pendingStartUtc = null;
                SetState(DuckingState.Idle);
                return;
            }

            if (_state == DuckingState.Ducking)
            {
                _hangStartUtc = DateTime.UtcNow;
            }
        }
    }

    private void EvaluateState(object? _)
    {
        lock (_stateLock)
        {
            if (_disposed || !IsEnabled)
            {
                if (_state != DuckingState.Idle)
                {
                    ReleaseBackgroundAudio();
                    _pendingStartUtc = null;
                    _hangStartUtc = null;
                    SetState(DuckingState.Idle);
                }

                return;
            }

            if (!SessionEnumerator.IsTargetAvailable(_settings.TargetProcess))
            {
                if (_state != DuckingState.Idle)
                {
                    ReleaseBackgroundAudio();
                    _pendingStartUtc = null;
                    _hangStartUtc = null;
                    SetState(DuckingState.Idle);
                }

                return;
            }

            if (_state == DuckingState.Pending && _pendingStartUtc is not null)
            {
                if (_peakMonitor.IsPlaying)
                {
                    var elapsedMs = (DateTime.UtcNow - _pendingStartUtc.Value).TotalMilliseconds;
                    var hasBackground = SessionEnumerator.HasActiveBackgroundAudio(_settings, _settings.TargetProcess, _selfPid);
                    if (elapsedMs >= _settings.DurationThresholdMs)
                    {
                        if (!hasBackground)
                        {
                            if (!_loggedPendingBlock)
                            {
                                _loggedPendingBlock = true;
                                DuckTrace.Write(_settings.TargetProcess, $"Pending blocked at {elapsedMs:F0}ms: no background audio on '{_settings.BackgroundAudioDevicePattern}'");
                            }
                        }
                        else if (!AcquireBackgroundAudio() || !_acquired)
                        {
                            if (!_loggedPendingBlock)
                            {
                                _loggedPendingBlock = true;
                                DuckTrace.Write(_settings.TargetProcess, $"Pending blocked at {elapsedMs:F0}ms: could not mute background sessions");
                            }
                        }
                        else
                        {
                            SetState(DuckingState.Ducking);
                        }
                    }
                }
                else
                {
                    DuckTrace.Write(_settings.TargetProcess, "Pending cancelled: peak monitor no longer playing");
                    _pendingStartUtc = null;
                    SetState(DuckingState.Idle);
                }
            }

            if (_state == DuckingState.Ducking)
            {
                if (_settings.RequireBackgroundAudio &&
                    !SessionEnumerator.HasActiveBackgroundAudio(_settings, _settings.TargetProcess, _selfPid) &&
                    !_peakMonitor.IsPlaying)
                {
                    ReleaseBackgroundAudio();
                    _hangStartUtc = null;
                    _pendingStartUtc = null;
                    SetState(DuckingState.Idle);
                    return;
                }

                if (_peakMonitor.IsPlaying)
                {
                    _hangStartUtc = null;
                }
                else
                {
                    _hangStartUtc ??= DateTime.UtcNow;
                    var hangMs = (DateTime.UtcNow - _hangStartUtc.Value).TotalMilliseconds;
                    if (hangMs >= _settings.HangTimeMs)
                    {
                        ReleaseBackgroundAudio();
                        _hangStartUtc = null;
                        _pendingStartUtc = null;
                        SetState(DuckingState.Idle);
                    }
                }
            }
        }
    }

    private bool AcquireBackgroundAudio()
    {
        if (_acquired)
        {
            return true;
        }

        _acquired = _coordinator.TryAcquire(_settings, _selfPid);
        return _acquired;
    }

    private void ReleaseBackgroundAudio()
    {
        if (!_acquired)
        {
            return;
        }

        _coordinator.Release();
        _acquired = false;
    }

    private void SetState(DuckingState state)
    {
        Action<DuckingState>? handler;
        lock (_stateLock)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
            handler = StateChanged;
        }

        DuckTrace.Write(_settings.TargetProcess, $"State -> {state}");
        UpdateStateTimerInterval();
        if (state == DuckingState.Idle)
        {
            _peakMonitor.SetFastPolling(false);
        }

        handler?.Invoke(state);
    }

    private void UpdateStateTimerInterval()
    {
        var interval = _state == DuckingState.Idle
            ? _settings.IdlePollIntervalMs
            : _settings.ActivePollIntervalMs;
        _stateTimer.Change(0, interval);
    }

    public void WriteDiagnosticSnapshot()
    {
        var peak = SessionEnumerator.GetPeakForTargetProcess(_settings.TargetProcess);
        var available = SessionEnumerator.IsTargetAvailable(_settings.TargetProcess);
        DuckTrace.WriteSnapshot(
            _settings.TargetProcess,
            _settings.TargetProcess,
            peak,
            available,
            _peakMonitor.RegisteredSessionHandlers,
            $"state={State} playing={_peakMonitor.IsPlaying}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _stateTimer.Dispose();
        _peakMonitor.Dispose();
    }
}

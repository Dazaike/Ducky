using Ducky.Core;

namespace Ducky.Core.Audio;

public sealed class PeakMonitor : IDisposable
{
    private string _targetProcess;
    private float _peakThreshold;
    private readonly int _pollIntervalMs;
    private readonly int _startConsecutivePolls;
    private readonly int _endDebounceMs;

    private int _idlePollIntervalMs = 300;
    private int _activePollIntervalMs = 30;
    private bool _fastPolling;
    private DateTime? _sessionWakeUntilUtc;
    private System.Threading.Timer? _timer;
    private readonly TargetSessionWatcher _sessionWatcher;
    private bool _isPlaying;
    private int _consecutiveAbove;
    private DateTime? _silenceStartUtc;
    private DateTime _eventStartUtc;
    private float _eventPeakMax;
    private bool _disposed;

    public PeakMonitor(
        string targetProcess,
        float peakThreshold = 0.01f,
        int pollIntervalMs = 30,
        int startConsecutivePolls = 2,
        int endDebounceMs = 100)
    {
        _targetProcess = targetProcess;
        _peakThreshold = peakThreshold;
        _pollIntervalMs = pollIntervalMs;
        _startConsecutivePolls = startConsecutivePolls;
        _endDebounceMs = endDebounceMs;
        _sessionWatcher = new TargetSessionWatcher(targetProcess);
        _sessionWatcher.SessionActivity += OnSessionActivity;
    }

    public string TargetProcess
    {
        get => _targetProcess;
        set
        {
            _targetProcess = value;
            _sessionWatcher.TargetProcess = value;
        }
    }

    public float PeakThreshold
    {
        get => _peakThreshold;
        set => _peakThreshold = value;
    }

    public bool IsPlaying => _isPlaying;

    public int RegisteredSessionHandlers => _sessionWatcher.RegisteredSessionCount;

    public DateTime? CurrentEventStartUtc => _isPlaying ? _eventStartUtc : null;

    public event Action? PlaybackStarted;

    public event Action<PlaybackEvent>? PlaybackCompleted;

    public void SetPollIntervals(int idleMs, int activeMs)
    {
        _idlePollIntervalMs = idleMs;
        _activePollIntervalMs = activeMs;
        ApplyPollInterval();
    }

    public void SetFastPolling(bool enabled)
    {
        if (_fastPolling == enabled)
        {
            return;
        }

        _fastPolling = enabled;
        ApplyPollInterval();
    }

    public void Start()
    {
        _timer ??= new System.Threading.Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
        _sessionWatcher.Start();
        ApplyPollInterval();
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _sessionWatcher.Stop();
        _fastPolling = false;
    }

    private void OnSessionActivity()
    {
        if (_disposed)
        {
            return;
        }

        // Session events fire before the peak meter rises — hold fast polling open.
        _sessionWakeUntilUtc = DateTime.UtcNow.AddSeconds(3);
        DuckTrace.Write(_targetProcess, "Session activity wake");
        if (!_fastPolling && !_isPlaying)
        {
            _fastPolling = true;
            ApplyPollInterval();
        }

        ThreadPool.QueueUserWorkItem(static state =>
        {
            if (state is PeakMonitor monitor)
            {
                monitor.Poll(null);
            }
        }, this);
    }

    private void ApplyPollInterval()
    {
        if (_timer is null)
        {
            return;
        }

        if (_fastPolling || _isPlaying)
        {
            _timer.Change(0, _activePollIntervalMs);
            return;
        }

        // Always keep a slow safety-net poll even when session events are registered.
        _timer.Change(0, _idlePollIntervalMs);
    }

    private void Poll(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!SessionEnumerator.IsTargetAvailable(_targetProcess))
            {
                DuckTrace.Write(_targetProcess, "Poll: target unavailable");
                if (_isPlaying)
                {
                    CompleteEvent(DateTime.UtcNow);
                }

                return;
            }

            var peak = SessionEnumerator.GetPeakForTargetProcess(_targetProcess);
            var now = DateTime.UtcNow;

            if (peak > _peakThreshold)
            {
                if (!_fastPolling)
                {
                    _fastPolling = true;
                    ApplyPollInterval();
                }

                _consecutiveAbove++;
                _silenceStartUtc = null;

                if (!_isPlaying && _consecutiveAbove >= _startConsecutivePolls)
                {
                    _isPlaying = true;
                    _eventStartUtc = now;
                    _eventPeakMax = peak;
                    DuckTrace.Write(_targetProcess, $"Playback started peak={peak:F4}");
                    PlaybackStarted?.Invoke();
                }
                else if (_isPlaying)
                {
                    _eventPeakMax = Math.Max(_eventPeakMax, peak);
                }
            }
            else
            {
                _consecutiveAbove = 0;

                if (_isPlaying)
                {
                    _silenceStartUtc ??= now;
                    var silenceMs = (now - _silenceStartUtc.Value).TotalMilliseconds;
                    if (silenceMs >= _endDebounceMs)
                    {
                        CompleteEvent(now);
                    }
                }
                else if (_fastPolling && !IsSessionWakeActive())
                {
                    _fastPolling = false;
                    ApplyPollInterval();
                }
            }
        }
        catch
        {
            // WASAPI sessions can disappear between polls.
        }
    }

    private void CompleteEvent(DateTime endUtc)
    {
        if (!_isPlaying)
        {
            return;
        }

        _isPlaying = false;
        _consecutiveAbove = 0;
        _silenceStartUtc = null;

        if (_fastPolling)
        {
            _fastPolling = false;
            ApplyPollInterval();
        }

        var playbackEvent = new PlaybackEvent
        {
            Start = _eventStartUtc,
            End = endUtc,
            DurationMs = (endUtc - _eventStartUtc).TotalMilliseconds,
            PeakMax = _eventPeakMax
        };

        DuckTrace.Write(_targetProcess, $"Playback completed duration={playbackEvent.DurationMs:F0}ms peakMax={_eventPeakMax:F4}");
        PlaybackCompleted?.Invoke(playbackEvent);
    }

    private bool IsSessionWakeActive() =>
        _sessionWakeUntilUtc is not null && DateTime.UtcNow < _sessionWakeUntilUtc.Value;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionWatcher.SessionActivity -= OnSessionActivity;
        _sessionWatcher.Dispose();
        _timer?.Dispose();
        _timer = null;
    }
}

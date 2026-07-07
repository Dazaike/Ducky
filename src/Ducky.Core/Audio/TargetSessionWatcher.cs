using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Ducky.Core;

namespace Ducky.Core.Audio;

/// <summary>
/// Registers IAudioSessionEvents on target-process sessions and listens for new sessions
/// via IAudioSessionNotification so PeakMonitor can wake on activity instead of idle polling.
/// </summary>
internal sealed class TargetSessionWatcher : IDisposable
{
    private readonly object _lock = new();
    private readonly List<DeviceListener> _deviceListeners = new();
    private readonly List<RegisteredSession> _registeredSessions = new();
    private readonly HashSet<string> _registeredSessionKeys = new(StringComparer.OrdinalIgnoreCase);

    private string _targetProcess;
    private bool _started;
    private bool _disposed;
    private bool _eventsAvailable;
    private System.Threading.Timer? _healthTimer;

    public TargetSessionWatcher(string targetProcess)
    {
        _targetProcess = targetProcess;
    }

    public string TargetProcess
    {
        get => _targetProcess;
        set
        {
            lock (_lock)
            {
                if (_targetProcess.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _targetProcess = value;
                if (_started)
                {
                    Resubscribe();
                }
            }
        }
    }

    public bool EventsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _eventsAvailable;
            }
        }
    }

    public int RegisteredSessionCount
    {
        get
        {
            lock (_lock)
            {
                return _registeredSessions.Count;
            }
        }
    }

    public event Action? SessionActivity;

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            try
            {
                Resubscribe();
                _eventsAvailable = _deviceListeners.Count > 0;
            }
            catch (Exception ex)
            {
                _eventsAvailable = false;
                DuckTrace.Write(_targetProcess, $"Resubscribe failed on start: {ex.Message}");
            }

            _healthTimer ??= new System.Threading.Timer(HealthCheck, null, 30_000, 30_000);
            _started = true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _healthTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            UnsubscribeAll();
            _started = false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _healthTimer?.Dispose();
            _healthTimer = null;
            UnsubscribeAll();
        }
    }

    private void HealthCheck(object? _)
    {
        if (_disposed || !_started)
        {
            return;
        }

        lock (_lock)
        {
            var before = _registeredSessions.Count;
            Resubscribe();
            var after = _registeredSessions.Count;
            if (before != after || after == 0)
            {
                DuckTrace.Write(
                    _targetProcess,
                    $"Health resubscribe: handlers {before} -> {after}, devices={_deviceListeners.Count}");
            }
        }
    }

    private void Resubscribe()
    {
        UnsubscribeAll();
        SessionEnumerator.InvalidateTargetDeviceCache(_targetProcess);

        foreach (var device in SessionEnumerator.GetDevicesForTargetMonitoring(_targetProcess))
        {
            var listener = new DeviceListener(this, device);
            listener.Subscribe();
            _deviceListeners.Add(listener);
        }

        DuckTrace.Write(_targetProcess, $"Resubscribed on {_deviceListeners.Count} device(s), {_registeredSessions.Count} session handler(s)");
    }

    private void UnsubscribeAll()
    {
        foreach (var listener in _deviceListeners)
        {
            listener.Dispose();
        }

        _deviceListeners.Clear();

        foreach (var registered in _registeredSessions)
        {
            registered.Dispose();
        }

        _registeredSessions.Clear();
        _registeredSessionKeys.Clear();
    }

    internal void OnTargetSessionActivity()
    {
        SessionActivity?.Invoke();
    }

    internal void UnregisterSession(string key)
    {
        lock (_lock)
        {
            _registeredSessionKeys.Remove(key);

            for (var i = _registeredSessions.Count - 1; i >= 0; i--)
            {
                if (!_registeredSessions[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _registeredSessions[i].Dispose();
                _registeredSessions.RemoveAt(i);
                break;
            }
        }

        DuckTrace.Write(_targetProcess, $"Session disconnected, removed handler key={key}");
    }

    internal void TryRegisterSession(AudioSessionControl session, MMDevice device)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var pid = session.GetProcessID;
            if (pid == 0)
            {
                return;
            }

            var processName = GetProcessName((int)pid);
            var targetName = SessionEnumerator.NormalizeProcessName(_targetProcess);
            if (!processName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var sessionId = GetSessionIdentifierSafe(session);
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = $"pid:{pid}";
            }

            var key = $"{device.ID}:{sessionId}";
            lock (_lock)
            {
                if (!_registeredSessionKeys.Add(key))
                {
                    return;
                }

                var handler = new SessionEventHandler(this, key);
                session.RegisterEventClient(handler);
                _registeredSessions.Add(new RegisteredSession(session, handler, key));
            }

            DuckTrace.Write(_targetProcess, $"Registered session handler key={key} pid={pid} state={session.State}");
        }
        catch (Exception ex)
        {
            DuckTrace.Write(_targetProcess, $"Register session failed: {ex.Message}");
        }
    }

    private static string GetSessionIdentifierSafe(AudioSessionControl session)
    {
        try
        {
            return session.GetSessionIdentifier ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class DeviceListener : IDisposable
    {
        private readonly TargetSessionWatcher _owner;
        private readonly MMDevice _device;
        private AudioSessionManager? _manager;

        public DeviceListener(TargetSessionWatcher owner, MMDevice device)
        {
            _owner = owner;
            _device = device;
        }

        public void Subscribe()
        {
            _manager = _device.AudioSessionManager;
            _manager.OnSessionCreated += OnSessionCreated;

            for (var i = 0; i < _manager.Sessions.Count; i++)
            {
                _owner.TryRegisterSession(_manager.Sessions[i], _device);
            }
        }

        private void OnSessionCreated(object sender, IAudioSessionControl newSession)
        {
            var control = new AudioSessionControl(newSession);
            SessionEnumerator.InvalidateTargetDeviceCache(_owner.TargetProcess);
            _owner.TryRegisterSession(control, _device);
            _owner.OnTargetSessionActivity();
        }

        public void Dispose()
        {
            if (_manager is not null)
            {
                _manager.OnSessionCreated -= OnSessionCreated;
            }
        }
    }

    private sealed class RegisteredSession : IDisposable
    {
        private readonly AudioSessionControl _session;
        private readonly SessionEventHandler _handler;

        public RegisteredSession(AudioSessionControl session, SessionEventHandler handler, string key)
        {
            _session = session;
            _handler = handler;
            Key = key;
        }

        public string Key { get; }

        public void Dispose()
        {
            try
            {
                _session.UnRegisterEventClient(_handler);
            }
            catch
            {
                // Session may already be gone.
            }
        }
    }

    private sealed class SessionEventHandler : IAudioSessionEventsHandler
    {
        private readonly TargetSessionWatcher _owner;
        private readonly string _sessionKey;

        public SessionEventHandler(TargetSessionWatcher owner, string sessionKey)
        {
            _owner = owner;
            _sessionKey = sessionKey;
        }

        public void OnStateChanged(AudioSessionState state)
        {
            if (state is AudioSessionState.AudioSessionStateActive
                or AudioSessionState.AudioSessionStateInactive)
            {
                DuckTrace.Write(_owner.TargetProcess, $"Session state -> {state}");
                _owner.OnTargetSessionActivity();
            }
        }

        public void OnVolumeChanged(float volume, bool isMuted) { }

        public void OnDisplayNameChanged(string displayName) { }

        public void OnIconPathChanged(string iconPath) { }

        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }

        public void OnGroupingParamChanged(ref Guid groupingId) { }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            DuckTrace.Write(_owner.TargetProcess, $"Session disconnected ({disconnectReason}) key={_sessionKey}");
            _owner.UnregisterSession(_sessionKey);
            _owner.OnTargetSessionActivity();
        }
    }
}

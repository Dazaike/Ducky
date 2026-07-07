using Ducky.Core.Config;

namespace Ducky.Core.Audio;

/// <summary>
/// Ensures only one mute/pause is applied even when multiple target apps duck at once.
/// </summary>
public sealed class SharedDuckingCoordinator
{
    private readonly object _lock = new();
    private readonly DuckingController _controller = new();
    private int _depth;
    private bool _pauseActive;

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _depth > 0;
            }
        }
    }

    public bool TryAcquire(DuckingSettings settings, uint selfPid)
    {
        lock (_lock)
        {
            if (_depth == 0)
            {
                if (IsPauseMode(settings))
                {
                    MediaKeySender.SendMediaPlayPause();
                    _pauseActive = true;
                }
                else
                {
                    _controller.ApplyDucking(settings.TargetProcess, selfPid, settings);
                    if (!_controller.IsDucked)
                    {
                        return false;
                    }
                }
            }

            _depth++;
            return true;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_depth <= 0)
            {
                return;
            }

            _depth--;
            if (_depth > 0)
            {
                return;
            }

            if (_pauseActive)
            {
                MediaKeySender.SendMediaPlayPause();
                _pauseActive = false;
            }
            else
            {
                _controller.RestoreAll();
            }
        }
    }

    public void ForceRestoreAll(DuckingSettings settings, uint selfPid)
    {
        lock (_lock)
        {
            _depth = 0;
            if (_pauseActive)
            {
                MediaKeySender.SendMediaPlayPause();
                _pauseActive = false;
            }

            _controller.ForceRestore(settings, selfPid);
        }
    }

    private static bool IsPauseMode(DuckingSettings settings) =>
        settings.DuckingMode.Equals("Pause", StringComparison.OrdinalIgnoreCase);
}

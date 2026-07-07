namespace Ducky.Core.Audio;

internal sealed class VolumeFadeTarget
{
    public required Func<float> GetVolume { get; init; }
    public required Action<float> SetVolume { get; init; }
    public required Action<bool> SetMute { get; init; }
    public float From { get; init; }
    public float To { get; init; }
    public bool RestoreMute { get; init; }
}

internal sealed class VolumeFader
{
    private const int StepMs = 20;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Snap(IReadOnlyList<VolumeFadeTarget> targets)
    {
        Cancel();
        foreach (var target in targets)
        {
            try
            {
                target.SetMute(false);
                target.SetVolume(target.To);
                target.SetMute(target.RestoreMute);
            }
            catch
            {
                // Session may have ended.
            }
        }
    }

    public void Fade(IReadOnlyList<VolumeFadeTarget> targets, int durationMs, Action? onComplete = null)
    {
        if (targets.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        if (durationMs <= 0)
        {
            Snap(targets);
            onComplete?.Invoke();
            return;
        }

        CancellationToken token;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var target in targets)
                {
                    try
                    {
                        target.SetMute(false);
                    }
                    catch
                    {
                        // Session may have ended.
                    }
                }

                var steps = Math.Max(1, durationMs / StepMs);
                for (var step = 1; step <= steps; step++)
                {
                    token.ThrowIfCancellationRequested();
                    var t = step / (float)steps;

                    foreach (var target in targets)
                    {
                        try
                        {
                            var value = target.From + ((target.To - target.From) * t);
                            target.SetVolume(Math.Clamp(value, 0f, 1f));
                        }
                        catch
                        {
                            // Session may have ended.
                        }
                    }

                    if (step < steps)
                    {
                        await Task.Delay(StepMs, token).ConfigureAwait(false);
                    }
                }

                foreach (var target in targets)
                {
                    try
                    {
                        target.SetMute(target.RestoreMute);
                    }
                    catch
                    {
                        // Session may have ended.
                    }
                }

                onComplete?.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer fade or emergency restore.
            }
        }, token);
    }
}

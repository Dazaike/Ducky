namespace Ducky.Core.Audio;

public sealed class PlaybackEvent
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public double DurationMs { get; init; }
    public float PeakMax { get; init; }

    public override string ToString() =>
        $"{Start:HH:mm:ss.fff}  {DurationMs:F0} ms  peak {PeakMax:F3}";

}

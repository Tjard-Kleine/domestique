using System.Diagnostics;

namespace Domestique.Core;

// Moving average of power over a time window, O(1) per sample.
public sealed class PowerAverager(TimeSpan window)
{
    private readonly Queue<(long Ts, int W)> _samples = new();
    private long _runningSum = 0;

    // timestamp must come from Stopwatch.GetTimestamp().
    public int Add(long timestamp, int watts)
    {
        _samples.Enqueue((timestamp, watts));
        _runningSum += watts;

        while (_samples.Count > 0 && Stopwatch.GetElapsedTime(_samples.Peek().Ts, timestamp) > window)
        {
            var (_, oldWatts) = _samples.Dequeue();
            _runningSum -= oldWatts;
        }

        if (_samples.Count == 0) return 0;

        return (int)Math.Round((double)_runningSum / _samples.Count);
    }
}

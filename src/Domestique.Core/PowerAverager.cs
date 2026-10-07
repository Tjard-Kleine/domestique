using System.Diagnostics;

namespace Domestique.Core;

public sealed class PowerAverager(TimeSpan window)
{
    private readonly Queue<(long Ts, int W)> _samples = new();
    private long _runningSum = 0; // Verwende long, um Überläufe bei der Summe zu verhindern

    public int Add(long timestamp, int watts)
    {
        // 1. Neuen Wert hinzufügen und Summe aktualisieren
        _samples.Enqueue((timestamp, watts));
        _runningSum += watts;

        // 2. Veraltete Werte entfernen (mit Absicherung gegen leere Queue)
        // Hinweis: timestamp MUSS von Stopwatch.GetTimestamp() stammen!
        while (_samples.Count > 0 && Stopwatch.GetElapsedTime(_samples.Peek().Ts, timestamp) > window)
        {
            var (_, oldWatts) = _samples.Dequeue();
            _runningSum -= oldWatts;
        }

        // 3. Durchschnitt in O(1) berechnen
        if (_samples.Count == 0) return 0;
        
        return (int)Math.Round((double)_runningSum / _samples.Count);
    }
}

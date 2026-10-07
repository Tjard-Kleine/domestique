namespace Domestique.Core;

public sealed record SessionSnapshot
{
    public int PowerW { get; init; }
    public int Power3sW { get; init; }
    public double? CadenceRpm { get; init; }         // null: Trainer liefert keine Trittfrequenz (z. B. D100)
    public int? HeartRateBpm { get; init; }
    public double TrainerSpeedKmh { get; init; }
    public long LastPacketTimestamp { get; init; }   // Stopwatch.GetTimestamp()
    public bool Connected { get; init; }
    public bool StrapConnected { get; init; }        // Puls kommt vom Gurt statt vom Trainer
    public double VirtualSpeedKmh { get; init; }     // aus der Physik, nicht vom Trainer (Review #12)
    public double DistanceM { get; init; }           // Gesamtdistanz der Fahrt
    public double GradePercent { get; init; }        // Steigung der Strecke an der aktuellen Position
}

public sealed class SessionState
{
    private SessionSnapshot _current = new();
    public SessionSnapshot Current => Volatile.Read(ref _current);

    public void Update(Func<SessionSnapshot, SessionSnapshot> change)
    {
        SessionSnapshot before, after;
        do { before = Current; after = change(before); }
        while (Interlocked.CompareExchange(ref _current, after, before) != before);
    }
}
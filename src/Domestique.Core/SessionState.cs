namespace Domestique.Core;

public sealed record SessionSnapshot
{
    public int PowerW { get; init; }
    public int Power3sW { get; init; }
    public double CadenceRpm { get; init; }
    public int? HeartRateBpm { get; init; }
    public double TrainerSpeedKmh { get; init; }
    public long LastPacketTimestamp { get; init; }   // Stopwatch.GetTimestamp()
    public bool Connected { get; init; }
    public bool StrapConnected { get; init; }        // ab Phase 8
    public double VirtualSpeedKmh { get; init; }     // ab Phase 5
    public double DistanceM { get; init; }           // ab Phase 5
    public double GradePercent { get; init; }        // ab Phase 5
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
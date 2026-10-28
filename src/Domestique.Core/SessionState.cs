namespace Domestique.Core;

public sealed record SessionSnapshot
{
    public int PowerW { get; init; }
    public int Power3sW { get; init; }
    public double? CadenceRpm { get; init; }         // null: the trainer sends no cadence (e.g. Van Rysel D100)
    public int? HeartRateBpm { get; init; }
    public long LastPacketTimestamp { get; init; }   // Stopwatch.GetTimestamp()
    public bool Connected { get; init; }
    public bool StrapConnected { get; init; }        // heart rate comes from a strap instead of the trainer
    public double VirtualSpeedKmh { get; init; }     // from the physics model, not from the trainer
    public double DistanceM { get; init; }           // total distance of the ride
}

// Thread-safe holder of the latest snapshot; BLE callbacks and the UI update it lock-free.
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

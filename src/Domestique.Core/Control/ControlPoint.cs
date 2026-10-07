using System.Diagnostics;
using System.Threading.Channels;
using Domestique.Core.Protocol;

namespace Domestique.Core.Control;

// Befehlsschlange für den FTMS Control Point (Review #2 und #3). Kennt kein Bluetooth: geschrieben wird über den
// übergebenen Delegaten, Antworten (Indications von 0x2AD9) und Status (0x2ADA) reicht der Aufrufer herein.
public sealed class ControlPoint : IAsyncDisposable
{
    private static readonly TimeSpan RegainCooldown = TimeSpan.FromSeconds(5);    // kein Ping-Pong mit einer anderen App

    private readonly Func<byte[], CancellationToken, Task<bool>> _write;
    private readonly TimeSpan _timeout;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);                             // immer nur ein Befehl unterwegs
    private readonly Channel<byte[]> _targets = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private Pending? _pending;
    private byte[]? _lastTarget;
    private long _lastRegain;

    public ControlPoint(Func<byte[], CancellationToken, Task<bool>> write, TimeSpan? timeout = null, Action<string>? log = null)
    {
        _write = write;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        _log = log ?? RawLog.Note;
        _pump = Task.Run(PumpAsync);
    }

    public bool HasControl { get; private set; }

    // Indication vom Control Point: nur die Antwort auf den gerade wartenden Befehl zählt.
    public void OnIndication(byte[] bytes)
    {
        if (ControlResponse.TryParse(bytes) is { } r && Volatile.Read(ref _pending) is { } p && p.OpCode == r.RequestOpCode)
            p.Response.TrySetResult(r);
    }

    // Einmaliger Befehl: senden und auf die passende Antwort warten. null = keine Antwort in der Zeit.
    public async Task<ControlResponse?> SendAsync(byte[] command)
    {
        await _gate.WaitAsync(_stop.Token);
        var pending = new Pending(command[0]);
        Volatile.Write(ref _pending, pending);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(_timeout);
        try
        {
            if (!await _write(command, timeout.Token)) return null;
            return await pending.Response.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { return null; }
        finally
        {
            Volatile.Write(ref _pending, null);
            _gate.Release();
        }
    }

    // Bei „Kontrolle nicht erlaubt“ (0x05) einmal neu anfordern und wiederholen.
    public async Task<bool> SendWithControlAsync(byte[] command)
    {
        var r = await SendAsync(command);
        if (r?.Result == 0x05 && await RequestControlAsync()) r = await SendAsync(command);
        if (r?.Success == true) HasControl = true;                               // manche Trainer beantworten Request Control nicht
        return r?.Success == true;
    }

    // Für Zielwerte (Watt, Steigung): der neueste ersetzt einen wartenden, damit sich nichts staut.
    public void SetTarget(byte[] command)
    {
        Volatile.Write(ref _lastTarget, command);
        _targets.Writer.TryWrite(command);
    }

    // Beim Verbinden und nach Kontrollverlust: Kontrolle holen, starten, letzten Zielwert erneut senden.
    public async Task<bool> TakeControlAsync()
    {
        try
        {
            if (!await RequestControlAsync()) { _log("Control Point: Kontrolle nicht erhalten"); return false; }
            await SendAsync(FtmsCommands.Start());
            if (Volatile.Read(ref _lastTarget) is { } t) _targets.Writer.TryWrite(t);
            return true;
        }
        catch (OperationCanceledException) { return false; }                     // beim Beenden
        catch (Exception ex) { _log("Control Point: " + ex.Message); return false; }
    }

    // Fitness Machine Status: nur 0xFF heißt Kontrolle verloren. 0x01 (Reset) schickt z. B. der D100 als Antwort
    // auf Request Control, das ist kein Verlust. Höchstens alle 5 s zurückholen.
    public Task OnMachineStatus(byte[] status)
    {
        if (status.Length == 0 || status[0] != 0xFF) return Task.CompletedTask;
        HasControl = false;
        long now = Stopwatch.GetTimestamp();
        if (_lastRegain != 0 && Stopwatch.GetElapsedTime(_lastRegain, now) < RegainCooldown) return Task.CompletedTask;
        _lastRegain = now;
        _log("Control Point: Kontrolle verloren, hole sie zurück");
        return TakeControlAsync();
    }

    // Verbindung weg: Kontrolle gilt als verloren, nach dem Reconnect holt TakeControlAsync sie zurück.
    public void OnDisconnected() => HasControl = false;

    private async Task<bool> RequestControlAsync() =>
        HasControl = (await SendAsync(FtmsCommands.RequestControl()))?.Success == true;

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var command in _targets.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    if (!await SendWithControlAsync(command))
                        _log($"Control Point: {Convert.ToHexString(command)} nicht bestätigt");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log("Control Point: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { }                                    // beim Beenden
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _targets.Writer.TryComplete();
        await _pump;
    }

    private sealed class Pending(byte opCode)
    {
        public byte OpCode => opCode;
        public TaskCompletionSource<ControlResponse> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

using System.Diagnostics;
using System.Threading.Channels;
using Domestique.Core.Protocol;

namespace Domestique.Core.Control;

// Command queue for the FTMS Control Point. Has no Bluetooth dependency: commands are written through the given
// delegate; the caller passes in indications (0x2AD9) and machine status (0x2ADA).
public sealed class ControlPoint : IAsyncDisposable
{
    private static readonly TimeSpan RegainCooldown = TimeSpan.FromSeconds(5);    // no ping-pong with another app

    private readonly Func<byte[], CancellationToken, Task<bool>> _write;
    private readonly TimeSpan _timeout;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);                             // one command in flight
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

    // Only the response to the pending command counts.
    public void OnIndication(byte[] bytes)
    {
        if (ControlResponse.TryParse(bytes) is { } r && Volatile.Read(ref _pending) is { } p && p.OpCode == r.RequestOpCode)
            p.Response.TrySetResult(r);
    }

    // Sends a command and waits for its response; null on timeout.
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

    // On "control not permitted" (0x05) requests control once and retries.
    public async Task<bool> SendWithControlAsync(byte[] command)
    {
        var r = await SendAsync(command);
        if (r?.Result == 0x05 && await RequestControlAsync()) r = await SendAsync(command);
        if (r?.Success == true) HasControl = true;                               // some trainers never answer Request Control
        return r?.Success == true;
    }

    // For targets (power, grade): the newest replaces a waiting one so nothing piles up.
    public void SetTarget(byte[] command)
    {
        Volatile.Write(ref _lastTarget, command);
        _targets.Writer.TryWrite(command);
    }

    // On connect and after losing control: request control, start, resend the last target.
    public async Task<bool> TakeControlAsync()
    {
        try
        {
            if (!await RequestControlAsync()) { _log("Control Point: control not granted"); return false; }
            await SendAsync(FtmsCommands.Start());
            if (Volatile.Read(ref _lastTarget) is { } t) _targets.Writer.TryWrite(t);
            return true;
        }
        catch (OperationCanceledException) { return false; }                     // shutting down
        catch (Exception ex) { _log("Control Point: " + ex.Message); return false; }
    }

    // Only status 0xFF means control was lost. 0x01 (reset) is e.g. the D100's reply to Request Control.
    // Takes control back at most every 5 s.
    public Task OnMachineStatus(byte[] status)
    {
        if (status.Length == 0 || status[0] != 0xFF) return Task.CompletedTask;
        HasControl = false;
        long now = Stopwatch.GetTimestamp();
        if (_lastRegain != 0 && Stopwatch.GetElapsedTime(_lastRegain, now) < RegainCooldown) return Task.CompletedTask;
        _lastRegain = now;
        _log("Control Point: control lost, taking it back");
        return TakeControlAsync();
    }

    // After a reconnect TakeControlAsync gets control back.
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
                        _log($"Control Point: {Convert.ToHexString(command)} not confirmed");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log("Control Point: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { }                                    // shutting down
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

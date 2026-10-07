using System.Collections.Concurrent;
using System.Diagnostics;
using Domestique.Core.Control;
using Domestique.Core.Protocol;
using Xunit;

namespace Domestique.Tests;

public class ControlTests
{
    [Fact]
    public void Simulation_matches_zwift_capture() =>
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00, 0x60, 0x01, 0x28, 0x33 }, FtmsCommands.SetSimulation(3.52));

    [Fact]
    public void Negative_grade() =>
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00, 0x06, 0xFF, 0x28, 0x33 }, FtmsCommands.SetSimulation(-2.5));

    [Fact]
    public void Target_power() =>
        Assert.Equal(new byte[] { 0x05, 0xC8, 0x00 }, FtmsCommands.SetTargetPower(200));

    [Fact]
    public void Erg_blocks_grade()
    {
        var sent = new List<byte[]>();
        var guard = new ModeGuard(sent.Add);
        guard.SetErg(200);
        guard.SetGrade(5);                                // muss ignoriert werden
        Assert.Single(sent);
        Assert.Equal(0x05, sent[0][0]);
    }

    [Fact]
    public void End_erg_returns_to_the_given_grade()
    {
        var sent = new List<byte[]>();
        var guard = new ModeGuard(sent.Add);
        guard.SetErg(200);
        guard.EndErg(3.52);                               // z. B. aktuelle Steigung der Strecke
        Assert.Equal(ControlMode.Sim, guard.Mode);
        Assert.Null(guard.TargetPowerW);
        Assert.Equal(FtmsCommands.SetSimulation(3.52), sent[^1]);
    }

    [Fact]
    public void Reapply_resends_erg_target()
    {
        var sent = new List<byte[]>();
        var guard = new ModeGuard(sent.Add);
        guard.SetErg(200);
        guard.Reapply();                                  // z. B. nach dem Verbinden
        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0], sent[1]);
    }

    [Fact]
    public void Reapply_without_mode_sends_flat_road()
    {
        var sent = new List<byte[]>();
        new ModeGuard(sent.Add).Reapply();
        Assert.Equal(FtmsCommands.SetSimulation(0), Assert.Single(sent));
    }

    [Fact]
    public async Task Newest_target_replaces_waiting_ones()
    {
        var hold = new TaskCompletionSource();
        var (cp, written) = FakeTrainer(hold: hold.Task);
        cp.SetTarget(FtmsCommands.SetTargetPower(100));
        await WaitUntil(() => written.Count == 1);        // 100 W ist unterwegs, der Trainer antwortet noch nicht
        cp.SetTarget(FtmsCommands.SetTargetPower(110));
        cp.SetTarget(FtmsCommands.SetTargetPower(120));
        hold.SetResult();
        await WaitUntil(() => written.Count == 2);
        await Task.Delay(50);
        Assert.Equal(new[] { 100, 120 }, written.Select(c => c[1] | c[2] << 8));
        await cp.DisposeAsync();
    }

    [Fact]
    public async Task Control_not_permitted_requests_control_and_retries()
    {
        int targets = 0;
        var (cp, written) = FakeTrainer(result: c => c[0] == 0x05 && targets++ == 0 ? (byte)0x05 : (byte)0x01);
        Assert.True(await cp.SendWithControlAsync(FtmsCommands.SetTargetPower(200)));
        Assert.Equal(new byte[] { 0x05, 0x00, 0x05 }, written.Select(c => c[0]));
        await cp.DisposeAsync();
    }

    [Fact]
    public async Task Answer_to_other_command_is_ignored()
    {
        ControlPoint cp = null!;
        cp = new ControlPoint((_, _) => { cp.OnIndication([0x80, 0x00, 0x01]); return Task.FromResult(true); },
            TimeSpan.FromMilliseconds(100), _ => { });
        Assert.Null(await cp.SendAsync(FtmsCommands.Start()));
        await cp.DisposeAsync();
    }

    [Fact]
    public async Task Lost_control_is_taken_back_once_with_last_target()
    {
        var (cp, written) = FakeTrainer();
        cp.SetTarget(FtmsCommands.SetTargetPower(200));
        await WaitUntil(() => written.Count == 1);
        await cp.OnMachineStatus([0xFF]);                 // z. B. Hersteller-App hat übernommen
        await WaitUntil(() => written.Count == 4);
        Assert.True(cp.HasControl);
        await cp.OnMachineStatus([0xFF]);                 // gleich danach: nicht erneut (kein Ping-Pong)
        await Task.Delay(50);
        Assert.Equal(new byte[] { 0x05, 0x00, 0x07, 0x05 }, written.Select(c => c[0]));
        await cp.DisposeAsync();
    }

    [Fact]
    public async Task Other_status_messages_are_ignored()
    {
        var (cp, written) = FakeTrainer();
        await cp.OnMachineStatus([0x01]);                 // Reset, schickt der D100 nach Request Control
        await cp.OnMachineStatus([0x04]);                 // gestartet
        await cp.OnMachineStatus([0x12, 0x00, 0x00, 0x00, 0x00, 0x28, 0x33]);   // Simulation geändert
        await Task.Delay(50);
        Assert.Empty(written);
        await cp.DisposeAsync();
    }

    // Simulierter Control Point: schreibt mit und antwortet per Indication (Standard: Erfolg)
    private static (ControlPoint Cp, ConcurrentQueue<byte[]> Written) FakeTrainer(Func<byte[], byte>? result = null, Task? hold = null)
    {
        var written = new ConcurrentQueue<byte[]>();
        ControlPoint cp = null!;
        cp = new ControlPoint(async (command, _) =>
        {
            written.Enqueue(command);
            if (hold is not null && written.Count == 1) await hold;
            cp.OnIndication([0x80, command[0], result?.Invoke(command) ?? 0x01]);
            return true;
        }, log: _ => { });
        return (cp, written);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Domestique.Core;
using Domestique.Core.Control;

namespace Domestique.App;

// Demo mode without a trainer: start with "--demo". No Bluetooth, nothing is saved to disk. A simulated trainer
// sends power, cadence and heart rate and is controlled with the keyboard (click the overlay first; H lists the keys).
// In ERG it holds the target like a real trainer.
public partial class MainWindow
{
    public static readonly bool Demo = Environment.GetCommandLineArgs().Contains("--demo", StringComparer.OrdinalIgnoreCase);

    // Middle of each zone as a share of FTP (Coggan: Z1 < 55, Z2 < 76, Z3 < 91, Z4 < 106, Z5 < 121, Z6 above)
    private static readonly double[] ZoneMiddle = [0, 0.45, 0.65, 0.83, 0.98, 1.13, 1.35];

    // Tour: each zone a few seconds, then 0 W and briefly no data (the avatar falls asleep)
    private static readonly (int Zone, double Seconds, bool Data)[] Tour =
        [(1, 5, true), (2, 5, true), (3, 5, true), (4, 5, true), (5, 5, true), (6, 5, true), (0, 4, true), (0, 6, false)];

    private readonly DispatcherTimer _demoTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Queue<int> _demoLast = new();
    private readonly Random _demoRng = new();
    private int _demoWatts, _demoTourStep = -1;
    private double _demoPower, _demoHeart = 72, _demoTourLeft;
    private bool _demoData = true, _demoConnected, _demoBluetoothOff;

    // Shows the "Recording started" notice in the demo too, without saving anything.
    private bool Recording => _ride.Current is not null || (Demo && _demoConnected && _demoData && _demoPower > 1);

    private void StartDemo()
    {
        RawLog.Note("Demo mode");
        _message = "Searching for trainer …";
        _demoWatts = (int)Math.Round(ZoneMiddle[1] * _settings.FtpW);
        _demoTimer.Tick += (_, _) => DemoTick();
        _demoTimer.Start();
        PreviewKeyDown += DemoKey;
        Motion.After(1200, () => _demoConnected = true);                   // search briefly, then connected
        Notice(NoticeKind.Info, "Demo · click the overlay, H for keys", 8);   // the notch is narrow: the key list is behind H
    }

    private void DemoTick()
    {
        if (_demoTourStep >= 0 && (_demoTourLeft -= 0.25) <= 0) NextTourStep();
        if (!_demoConnected || !_demoData)
        {
            if (!_demoConnected && _state.Current.Connected) _state.Update(s => s with { Connected = false });
            return;                                                         // no packets: "no data" after 3 s
        }
        // like a trainer: approaches the target (the ERG target in ERG) smoothly, with some noise
        int target = _guard.Mode == ControlMode.Erg && _guard.TargetPowerW is int erg ? erg : _demoWatts;
        _demoPower += (target - _demoPower) * 0.35;
        int power = target <= 0 && _demoPower < 3 ? 0 : (int)Math.Round(Math.Max(0, _demoPower + _demoRng.Next(-4, 5)));
        _demoLast.Enqueue(power);
        if (_demoLast.Count > 12) _demoLast.Dequeue();                      // 3 s average like a real trainer
        int avg = (int)Math.Round(_demoLast.Average());
        _demoHeart += ((power > 0 ? 95 + power * 0.32 : 72) - _demoHeart) * 0.03;   // heart rate follows slowly
        int? cadence = power > 0 ? 82 + power / 40 + _demoRng.Next(-2, 3) : 0;
        _state.Update(s => s with
        {
            Connected = true, PowerW = power, Power3sW = avg, CadenceRpm = cadence,
            HeartRateBpm = (int)Math.Round(_demoHeart), LastPacketTimestamp = Stopwatch.GetTimestamp(),
        });
    }

    private void DemoKey(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int zone = e.Key switch
        {
            >= Key.D1 and <= Key.D6 => e.Key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad6 => e.Key - Key.NumPad0,
            _ => 0,
        };
        if (zone > 0) { StopTour(); SetDemoZone(zone); }
        else switch (e.Key)
        {
            case Key.Up: StopTour(); _demoWatts += shift ? 50 : 10; break;
            case Key.Down: StopTour(); _demoWatts = Math.Max(0, _demoWatts - (shift ? 50 : 10)); break;
            case Key.D0 or Key.NumPad0: StopTour(); _demoWatts = 0; break;
            case Key.S: StopTour(); _demoData = !_demoData; break;
            case Key.V:
                _demoConnected = !_demoConnected;
                if (!_demoConnected) _state.Update(s => s with { Connected = false });
                break;
            case Key.B: DemoBluetooth(!_demoBluetoothOff); break;
            case Key.A: if (_demoTourStep >= 0) StopTour(); else { _demoTourStep = -1; NextTourStep(); } break;
            case Key.H: Notice(NoticeKind.Info, "1–6 zone · ↑↓ W · 0 stop · S data · V link · B BT · A tour", 10); break;
            default: return;
        }
        e.Handled = true;
        Render(_state.Current);
    }

    private void SetDemoZone(int zone)
    {
        _demoData = true;
        _demoWatts = (int)Math.Round(ZoneMiddle[zone] * _settings.FtpW);
    }

    // Bluetooth off: link lost and Bluetooth screen. On again: search briefly, then connected.
    private void DemoBluetooth(bool off)
    {
        _demoBluetoothOff = off;
        if (off)
        {
            _demoConnected = false;
            _state.Update(s => s with { Connected = false });
        }
        OnBluetoothChanged(!off);
        if (!off) Motion.After(1500, () => { if (!_demoBluetoothOff) _demoConnected = true; });
    }

    private void NextTourStep()
    {
        _demoTourStep = (_demoTourStep + 1) % Tour.Length;
        var (zone, seconds, data) = Tour[_demoTourStep];
        _demoTourLeft = seconds;
        _demoData = data;
        _demoWatts = zone == 0 ? 0 : (int)Math.Round(ZoneMiddle[zone] * _settings.FtpW);
    }

    private void StopTour() => _demoTourStep = -1;
}

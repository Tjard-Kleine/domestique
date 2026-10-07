using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Domestique.Ble;
using Domestique.Core;
using Domestique.Core.Protocol;
using Domestique.Core.Control;
// ▸ Phase 4–7: weitere usings hier

namespace Domestique.App;

public partial class MainWindow : Window
{
    private const int HotkeyClickThrough = 1;                 // Strg+Alt+D
    private const int HotkeyPowerUp = 2, HotkeyPowerDown = 3; // Strg+Alt+Bild↑ / Bild↓
    // ▸ Phase 4: weitere Hotkey-Nummern hier

    private readonly SessionState _state = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private BleTrainer? _trainer;
    private bool _clickThrough;
    private string _message = "Suche Trainer …";
    private readonly ModeGuard _guard;
    // ▸ Phase 4–8: weitere Felder hier

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer.Tick += (_, _) => Render(_state.Current);
        SizeChanged += (_, e) =>                                            // unterer Rand bleibt stehen
        {
            if (e.HeightChanged && e.PreviousSize.Height > 0) Top -= e.NewSize.Height - e.PreviousSize.Height;
        };
        _guard = new ModeGuard(command => _trainer?.Control?.SetTarget(command));
        // ▸ Phase 4–7: weitere Initialisierung hier
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = Native.Handle(this);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        RegisterHotkey(hwnd, HotkeyClickThrough, 0x44);       // D
        RegisterHotkey(hwnd, HotkeyPowerUp, 0x21);            // Bild↑ (nicht Pfeiltasten: drehen bei manchen Treibern den Bildschirm)
        RegisterHotkey(hwnd, HotkeyPowerDown, 0x22);          // Bild↓
        // ▸ Phase 4: weitere Hotkeys registrieren
    }

    private static void RegisterHotkey(IntPtr hwnd, int id, uint key)
    {
        if (!Native.RegisterHotKey(hwnd, id, Native.MOD_CONTROL | Native.MOD_ALT, key))
            RawLog.Note($"Tastenkürzel Strg+Alt+0x{key:X2} ist von einem anderen Programm belegt");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Left is double left && _settings.Top is double top) { Left = left; Top = top; }
        else PlaceBottomRight();
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);   // Review #9
        _uiTimer.Start();
        Native.KeepTopmost(this);                                           // Review #7
        await ConnectTrainerAsync();
        // ▸ Phase 5, 7, 8: hier starten
    }

    private async Task ConnectTrainerAsync()
    {
        try
        {
            ulong? address = _settings.TrainerAddress
                ?? await BleScan.FindFirstAsync(BleUuids.FitnessMachineService, TimeSpan.FromSeconds(15));
            if (address is null) { _message = "Kein Trainer gefunden"; return; }
            _message = "Verbinde …";
            _trainer = new BleTrainer(_state);
            await _trainer.ConnectAsync(address.Value);
            _settings.TrainerAddress = address;
            _settings.Save();
            if (_trainer.PowerRange is { } range) _guard.PowerLimits = range;
            _guard.Reapply();                                 // Startzustand flache Straße oder das schon gewählte Ziel
        }
        catch (Exception ex)
        {
            if (_trainer is not null) { await _trainer.DisposeAsync(); _trainer = null; }   // halbe Verbindung freigeben
            _message = "Fehler: " + ex.Message;
            _settings.TrainerAddress = null;                  // beim nächsten Start neu suchen
            _settings.Save();
        }
    }

    private void PlaceBottomRight()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private void Render(SessionSnapshot s)
    {
        bool stale = Stopwatch.GetElapsedTime(s.LastPacketTimestamp) > TimeSpan.FromSeconds(3);
        PowerText.Text = stale ? "– W" : $"{s.Power3sW} W";
        DetailText.Text = $"{s.CadenceRpm:0} U/min" + (s.HeartRateBpm is int hr ? $"   {hr} bpm" : "");
        if (_guard.TargetPowerW is int target) DetailText.Text += $"   Ziel {target} W";
        StatusText.Text = !s.Connected ? _message
            : stale ? "keine Daten"
            : _trainer?.Control is null ? "Trainer nicht steuerbar"
            : !_trainer.Control.HasControl ? "keine Steuerung (andere App aktiv?)"
            : "";
        // ▸ Phase 4–5: weitere Anzeigen hier
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY) { OnHotkey(wParam.ToInt32()); handled = true; }
        return IntPtr.Zero;
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyClickThrough:
                _clickThrough = !_clickThrough;
                Native.SetClickThrough(this, _clickThrough);
                break;
            case HotkeyPowerUp:   _guard.SetErg((_guard.TargetPowerW ?? 150) + 10); break;
            case HotkeyPowerDown: _guard.SetErg((_guard.TargetPowerW ?? 150) - 10); break;
            // ▸ Phase 4: weitere Hotkeys hier
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ▸ Phase 4–8: weitere Methoden hier

    protected override void OnClosing(CancelEventArgs e)
    {
        var hwnd = Native.Handle(this);
        for (int id = 1; id <= 10; id++) Native.UnregisterHotKey(hwnd, id);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        // ▸ Phase 7: Fahrt exportieren
        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        if (_trainer is not null) await _trainer.DisposeAsync();
        // ▸ Phase 8: Pulsgurt freigeben
        base.OnClosed(e);
    }
}
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Domestique.Ble;
using Domestique.Core;
using Domestique.Core.Control;
using Domestique.Core.Library;
using Domestique.Core.Physics;
using Domestique.Core.Protocol;
using Domestique.Core.Recording;
using Domestique.Core.Routes;

namespace Domestique.App;

public partial class MainWindow : Window
{
    private const int HotkeyClickThrough = 1;                 // Strg+Alt+D
    private const int HotkeyPowerUp = 2, HotkeyPowerDown = 3; // Strg+Alt+Bild↑ / Bild↓
    private const int HotkeyEndErg = 4;                       // Strg+Alt+Ende (nicht E: AltGr+E ist das €-Zeichen)
    private const int HotkeySettings = 6;                     // Strg+Alt+O
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);
    private static readonly string RidesFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Domestique");
    private static readonly long AppStart = Stopwatch.GetTimestamp();     // monotone Zeitbasis für die Aufzeichnung

    private readonly SessionState _state = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly LibraryStore _library = new(AppSettings.Folder);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };   // ein Takt für alles
    private readonly ModeGuard _guard;
    private readonly RouteSim _sim = new(new RiderPhysics());
    private readonly GradeLimiter _limiter = new();
    private readonly RideRecorder _ride = new(RidesFolder);
    private BleTrainer? _trainer;
    private BleHeartRate? _strap;
    private SettingsWindow? _settingsWindow;
    private Func<RoutePoint, Point>? _mapProject, _profileProject;
    private bool _clickThrough, _closing, _trainerLoop, _strapLoop, _connectingTrainer, _connectingStrap;
    private string _message = "Suche Trainer …";
    private string? _lastTrainerError, _lastStrapError;
    private long _lastTick;
    private int _ticks;

    public MainWindow()
    {
        InitializeComponent();
        _guard = new ModeGuard(command => _trainer?.Control?.SetTarget(command));
        _timer.Tick += (_, _) => OnTick();
        SizeChanged += (_, e) =>                                            // unterer Rand bleibt stehen
        {
            if (e.HeightChanged && e.PreviousSize.Height > 0) Top -= e.NewSize.Height - e.PreviousSize.Height;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = Native.Handle(this);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        RegisterHotkey(hwnd, HotkeyClickThrough, 0x44);       // D
        RegisterHotkey(hwnd, HotkeyPowerUp, 0x21);            // Bild↑ (nicht Pfeiltasten: drehen bei manchen Treibern den Bildschirm)
        RegisterHotkey(hwnd, HotkeyPowerDown, 0x22);          // Bild↓
        RegisterHotkey(hwnd, HotkeyEndErg, 0x23);             // Ende
        RegisterHotkey(hwnd, HotkeySettings, 0x4F);           // O
    }

    private static void RegisterHotkey(IntPtr hwnd, int id, uint key)
    {
        if (!Native.RegisterHotKey(hwnd, id, Native.MOD_CONTROL | Native.MOD_ALT, key))
            RawLog.Note($"Tastenkürzel Strg+Alt+0x{key:X2} ist von einem anderen Programm belegt");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RawLog.Note($"Domestique gestartet {DateTime.Now:yyyy-MM-dd}");
        ApplySettings();                                                   // Farben und Größe vor dem Positionieren
        if (_settings.Left is double left && _settings.Top is double top && IsOnScreen(left, top)) { Left = left; Top = top; }
        else PlaceBottomRight();
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);   // Review #9
        Native.KeepTopmost(this);                                           // Review #7
        RideRecorder.RecoverUnexported(RidesFolder);
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
        _ = KeepTrainerConnectedAsync();
    }

    // ---------- Verbindungen ----------

    // Sucht und verbindet, bis es klappt: Der Trainer darf auch erst nach der App eingeschaltet werden.
    private async Task KeepTrainerConnectedAsync()
    {
        if (_trainerLoop) return;
        _trainerLoop = true;
        try
        {
            while (_trainer is null && !_closing)
            {
                if (await TryConnectTrainerAsync()) break;
                if (!_strapLoop) _ = KeepStrapConnectedAsync();             // Gurt erst nach dem ersten Trainer-Versuch
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
        finally { _trainerLoop = false; }
        if (!_strapLoop) _ = KeepStrapConnectedAsync();
    }

    private async Task<bool> TryConnectTrainerAsync()
    {
        if (_connectingTrainer || _trainer is not null) return _trainer is not null;
        _connectingTrainer = true;
        var trainer = new BleTrainer(_state);
        try
        {
            _message = "Suche Trainer …";
            ulong? address = _settings.TrainerAddress
                ?? await BleScan.FindFirstAsync(BleUuids.FitnessMachineService, TimeSpan.FromSeconds(15));
            if (address is null) { _message = "Kein Trainer gefunden, suche weiter …"; return false; }
            _message = "Verbinde …";
            await trainer.ConnectAsync(address.Value);
            _trainer = trainer;
            _lastTrainerError = null;
            _settings.TrainerAddress = address;
            _settings.Save();
            if (trainer.PowerRange is { } range) _guard.PowerLimits = range;
            _guard.Reapply();                                               // Startzustand flache Straße oder das schon gewählte Ziel
            _message = "Verbindung verloren, warte auf Trainer …";          // erscheint nur, falls sie später abreißt
            return true;
        }
        catch (Exception ex)
        {
            await trainer.DisposeAsync();                                   // halbe Verbindung freigeben
            if (ex.Message != _lastTrainerError) RawLog.Note("Trainer: " + ex.Message);   // bei Dauerfehlern nicht alle 10 s
            _lastTrainerError = ex.Message;
            _message = "Fehler: " + ex.Message;
            _settings.TrainerAddress = null;                                // beim nächsten Versuch neu suchen
            _settings.Save();
            return false;
        }
        finally { _connectingTrainer = false; }
    }

    // Puls vom Gurt hat Vorrang vor dem Puls des Trainers. Ohne Gurt läuft alles wie bisher.
    // Ein bekannter Gurt wird alle 30 s erneut versucht (z. B. erst nach dem Start angelegt). Ohne bekannten Gurt
    // wird nur einmal gesucht, weil dauerndes Scannen die laufende Trainer-Verbindung stören kann.
    private async Task KeepStrapConnectedAsync()
    {
        if (_strapLoop) return;
        _strapLoop = true;
        try
        {
            while (_strap is null && !_closing)
            {
                if (await TryConnectStrapAsync() || _settings.StrapAddress is null) break;
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
        finally { _strapLoop = false; }
    }

    private async Task<bool> TryConnectStrapAsync()
    {
        if (_connectingStrap || _strap is not null) return _strap is not null;
        _connectingStrap = true;
        var strap = new BleHeartRate(_state);
        try
        {
            ulong? address = _settings.StrapAddress
                ?? await BleScan.FindFirstAsync(BleUuids.HeartRateService, TimeSpan.FromSeconds(10));
            if (address is null) return false;                              // kein Gurt in der Nähe
            await strap.ConnectAsync(address.Value);
            _strap = strap;
            _settings.StrapAddress = address;
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            await strap.DisposeAsync();                                     // Adresse bleibt: Gurt evtl. nur noch nicht angelegt
            if (ex.Message != _lastStrapError) RawLog.Note("Pulsgurt: " + ex.Message);
            _lastStrapError = ex.Message;
            return false;
        }
        finally { _connectingStrap = false; }
    }

    public async Task<bool> ReconnectTrainerAsync()
    {
        if (_trainer is not null) { await _trainer.DisposeAsync(); _trainer = null; }
        _message = "Verbinde neu …";
        bool ok = await TryConnectTrainerAsync();                          // der Modus-Wächter schickt dem neuen Trainer das aktuelle Ziel
        if (!ok) _ = KeepTrainerConnectedAsync();
        return ok;
    }

    public async Task<bool> ReconnectStrapAsync()
    {
        if (_strap is not null) { await _strap.DisposeAsync(); _strap = null; }
        bool ok = await TryConnectStrapAsync();
        if (!ok) _ = KeepStrapConnectedAsync();
        return ok;
    }

    // ---------- Takt: Physik 4×/s, Anzeige 2×/s, Steigung, Karte und Aufzeichnung 1×/s ----------

    private void OnTick()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds, 1);
        _lastTick = now;

        var s = _state.Current;
        bool stale = IsStale(s);
        _sim.Step(stale ? 0 : s.PowerW, dt);                                // Review #5: ohne Daten keine alten Watt
        _state.Update(x => x with
        {
            VirtualSpeedKmh = _sim.SpeedMps * 3.6,
            DistanceM = _sim.DistanceM,
            GradePercent = _sim.GradePercent,
        });
        Record(_state.Current, stale);

        _ticks++;
        if (_ticks % 2 == 0) Render(_state.Current);
        if (_ticks % 4 != 0) return;
        if (_sim.Route is not null)
            _guard.SetGrade(_limiter.Next(_sim.GradePercent * _settings.Difficulty, 1));   // im ERG ignoriert (Review #1)
        MoveDots();
    }

    private static bool IsStale(SessionSnapshot s) => Stopwatch.GetElapsedTime(s.LastPacketTimestamp) > StaleAfter;

    private void Render(SessionSnapshot s)
    {
        bool stale = IsStale(s);
        PowerText.Text = stale ? "– W" : $"{s.Power3sW} W";

        var details = new List<string>();
        if (s.CadenceRpm is double cadence) details.Add($"{(stale ? 0 : cadence):0} U/min");    // nur, wenn der Trainer sie liefert
        if (s.HeartRateBpm is int hr) details.Add($"{hr} bpm");
        if (_guard.TargetPowerW is int target) details.Add($"Ziel {target} W");
        if (_ride.Elapsed(Stopwatch.GetElapsedTime(AppStart)) is { } t)
            details.Add($"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");               // Fahrzeit
        DetailText.Text = string.Join("   ", details);

        RouteText.Text = _sim.Route is { } r
            ? $"{s.VirtualSpeedKmh:0.0} km/h   {Math.Min(_sim.RouteDistanceM, r.LengthM) / 1000:0.0} / {r.LengthM / 1000:0.0} km   {s.GradePercent:+0.0;-0.0;0.0} %"
            : $"{s.VirtualSpeedKmh:0.0} km/h   {s.DistanceM / 1000:0.00} km";

        StatusText.Text = !s.Connected ? _message
            : stale ? "keine Daten"
            : _trainer?.Control is null ? "Trainer nicht steuerbar"
            : !_trainer.Control.HasControl ? "keine Steuerung (andere App aktiv?)"
            : _sim.Finished ? "Ziel erreicht"
            : "";
    }

    // ---------- Aufzeichnung (Phase 7) ----------

    // Speed und Distanz aus der Physik, nicht vom Trainer (Review #12).
    private void Record(SessionSnapshot s, bool stale) =>
        _ride.Tick(Stopwatch.GetElapsedTime(AppStart), DateTime.UtcNow, stale ? 0 : s.PowerW, stale ? 0 : s.CadenceRpm ?? 0,
                   s.HeartRateBpm, _sim.SpeedMps, s.DistanceM);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = _ride.Export() is { } file ? "Gespeichert: " + file : "Noch keine Fahrt aufgezeichnet.";
            MessageBox.Show(this, text, "Domestique");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export fehlgeschlagen"); }
    }

    // ---------- Strecken und Karte (Phase 5, 6, 9) ----------

    private void LoadRoute_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GPX-Strecke (*.gpx)|*.gpx" };
        if (dialog.ShowDialog(this) == true) ImportAndLoad(dialog.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var file in files) ImportAndLoad(file);
    }

    private void ImportAndLoad(string path)
    {
        try { LoadFile(_library.Import(path)); }                           // landet automatisch in der Bibliothek
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Datei nicht lesbar"); }
    }

    public void LoadFile(string path)
    {
        try
        {
            var route = GpxLoader.Load(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
            _sim.Load(route);
            MapLine.Points = RouteGeometry.Map(route, 90, 90, out _mapProject);
            ProfileLine.Points = RouteGeometry.Profile(route, 130, 90, out _profileProject);
            ApplySettings();                                                // zeigt die Karte, falls eingeschaltet
            MoveDots();
            RawLog.Note($"Strecke geladen: {route.Name}, {route.LengthM / 1000:0.0} km");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Strecke nicht lesbar"); }
    }

    private void MoveDots()
    {
        if (_sim.Route is not { } route || _mapProject is null || _profileProject is null || RoutePanel.Visibility != Visibility.Visible) return;
        var p = route.At(_sim.RouteDistanceM);
        var m = _mapProject(p);
        Canvas.SetLeft(MapDot, m.X - 4);
        Canvas.SetTop(MapDot, m.Y - 4);
        var q = _profileProject(p);
        Canvas.SetLeft(ProfileDot, q.X - 4);
        Canvas.SetTop(ProfileDot, q.Y - 4);
    }

    // ---------- Einstellungen (Phase 9) ----------

    public void ApplySettings()
    {
        var background = ParseColor(_settings.BackgroundColor, Colors.Black);
        background.A = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, 0, 1) * 255);
        Card.Background = Frozen(background);

        var text = Frozen(ParseColor(_settings.TextColor, Colors.White));
        foreach (var block in new[] { GearText, PowerText, DetailText, RouteText, StatusText }) block.Foreground = text;
        MapLine.Stroke = ProfileLine.Stroke = text;
        MapDot.Fill = ProfileDot.Fill = Frozen(ParseColor(_settings.AccentColor, Colors.OrangeRed));

        double scale = Math.Clamp(_settings.Scale, 0.5, 2);
        Card.LayoutTransform = new ScaleTransform(scale, scale);
        Width = 260 * scale;
        RoutePanel.Visibility = _settings.ShowMap && _sim.Route is not null ? Visibility.Visible : Visibility.Collapsed;

        _sim.Physics.MassKg = _settings.RiderKg + _settings.BikeKg;
        _sim.Physics.CdA = Math.Clamp(_settings.CdA, 0.1, 1);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string text, Color fallback)
    {
        try { return ColorConverter.ConvertFromString(text) is Color c ? c : fallback; }
        catch { return fallback; }                                         // Tippfehler in settings.json
    }

    private void OpenSettings()
    {
        if (_clickThrough) { _clickThrough = false; Native.SetClickThrough(this, false); }
        if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(this, _settings, _library);
        _settingsWindow.Show();
    }

    private void Gear_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // sonst startet das Verschieben
        OpenSettings();
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e) => OpenSettings();

    public void ResetPosition()
    {
        _settings.Left = _settings.Top = null;
        PlaceBottomRight();
    }

    private void PlaceBottomRight()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    // Gespeicherte Position auf einem inzwischen abgesteckten Monitor: lieber neu platzieren.
    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 50
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    // ---------- Tastenkürzel und Menü ----------

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
            case HotkeyEndErg:    EndErg(); break;
            case HotkeySettings:  OpenSettings(); break;
        }
    }

    // Zurück in die Simulation, mit der Steigung, die gerade am Trainer anliegen soll.
    private void EndErg() => _guard.EndErg(_sim.Route is null ? 0 : _limiter.Current);

    private void EndErg_Click(object sender, RoutedEventArgs e) => EndErg();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _timer.Stop();                                                      // keine neue Aufzeichnung nach dem Export
        var hwnd = Native.Handle(this);
        for (int id = 1; id <= 10; id++) Native.UnregisterHotKey(hwnd, id);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        try { _ride.Export(); }
        catch (Exception ex) { RawLog.Note("Export beim Beenden fehlgeschlagen: " + ex.Message); }   // die CSV bleibt, Rettung beim nächsten Start
        _settingsWindow?.Close();
        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_trainer is not null) await _trainer.DisposeAsync();            // sonst bleibt der Trainer für andere Apps belegt
        if (_strap is not null) await _strap.DisposeAsync();
        base.OnClosed(e);
    }
}

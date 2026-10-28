using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private const double HudW = 401, MapW = 328;              // notch as in the design; the expanded map adds 8 + 320
    private const double BaseWidth = HudW + MapW + 28 + 16;   // + 2 × 14 notch ears + room for the width spring
    private const double SnapDistance = 28, CenterSnap = 90;  // docking at the top edge, snapping to the center
    private const double Overhang = 12;                       // transparent bottom margin (Root.Margin): room for the open spring
    private const double TabStep = 36;                        // width of a header tab including spacing
    private const double StartW = 288;                        // small pill the notch opens from at launch
    private const double IntroW = 720;                        // intro width before it narrows to the HUD
    private const double CompactH = 36, CompactR = 14;        // minimized notch; its width depends on the chosen values
    private const double NotchW = 184;                        // width it retracts to when quitting
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);
    private static readonly string RidesFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Domestique");
    private static readonly long AppStart = Stopwatch.GetTimestamp();     // monotonic time base for recording

    // zone colors as in Zwift
    private static readonly Brush[] ZoneBrushes = new[] { "#7F7F7F", "#7F7F7F", "#338CFF", "#59BF59", "#FFCC3F", "#FF6639", "#FF3B30" }
        .Select(c => (Brush)Frozen((Color)ColorConverter.ConvertFromString(c))).ToArray();

    private readonly SessionState _state = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly LibraryStore _library = new(AppSettings.Folder);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };   // one tick for everything
    private readonly DispatcherTimer _noticeTimer = new();
    private (NoticeKind Kind, string Text, double Seconds)? _pendingNotice;     // notice waiting for the launch to finish
    private long _deviceSeen;                                                    // last time any device was connected (trainer or strap)
    private bool _asleep = true, _wasRecording;
    private readonly ModeGuard _guard;
    private readonly RouteSim _sim = new(new RiderPhysics());
    private readonly GradeLimiter _limiter = new();
    private readonly RideRecorder _ride = new(RidesFolder);
    private readonly Dictionary<HotkeyAction, bool> _hotkeyOk = [];      // false: another app already uses the shortcut
    private readonly List<long> _pokes = new();                           // times of the last slaps on the avatar
    private readonly Island _island;
    private readonly Tracked _shellWidth = new(HudW);                     // width while the map expands or collapses
    private readonly FrameLoop _widthLoop;
    private bool _greeting, _compact, _anchorTop, _switching, _mapOpen, _riding;
    private Color _surface;                                        // background with the chosen opacity
    private bool _opaque = true;                                   // fully opaque while no trainer is connected
    private BleTrainer? _trainer;
    private BleHeartRate? _strap;
    private SettingsWindow? _settingsWindow;
    private Func<RoutePoint, Point>? _mapProject;
    private bool _clickThrough, _closing, _trainerLoop, _strapLoop, _connectingTrainer, _connectingStrap;
    private bool _wasConnected, _everConnected, _hadControl, _controlLost, _strapWas, _wasFinished, _dockCentered;
    private string _message = "Searching for trainer …";
    private string? _lastTrainerError, _lastStrapError;
    private bool _bluetoothOff, _bluetoothCardDismissed, _bluetoothCardShown, _exiting;
    private Windows.Devices.Radios.Radio? _radio;                  // kept alive, otherwise the state events stop
    private TaskCompletionSource _wake = new();                    // wakes the trainer search as soon as Bluetooth is on
    private string? _activeRoute;                                  // file, "app:Name" or null = no route
    private long _lastTick;
    private int _ticks;

    static MainWindow() => Motion.EnableInteractions();               // press and toggle animations in all windows

    public MainWindow()
    {
        InitializeComponent();
        BoltIcon.Data = MiniBoltIcon.Data = Icons.Bolt;
        HeartIcon.Data = MiniHeartIcon.Data = Icons.HeartPulse;
        CadenceIcon.Data = MiniCadenceIcon.Data = Icons.Crank;
        SpeedIcon.Data = MiniSpeedIcon.Data = Icons.Speed;
        DistanceIcon.Data = MiniDistanceIcon.Data = Icons.Distance;
        ClimbIcon.Data = MiniClimbIcon.Data = Icons.Climb;
        TimeIcon.Data = MiniTimeIcon.Data = Icons.Clock;
        _widthLoop = new FrameLoop((dt, now) =>
        {
            _shellWidth.Step(dt, now);
            Shell.Width = _shellWidth.Value;
            return _shellWidth.Animating;
        });
        Ride.Landing += Ride_Landing;
        _guard = new ModeGuard(command => _trainer?.Control?.SetTarget(command));
        _timer.Tick += (_, _) => OnTick();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); Motion.Run(NoticeBar, OpacityProperty, NoticeBar.Opacity, 0, 350, Co.InOutE); };
        _island = new Island(Shell, LeftEar, RightEar);
        Greeting.ChooseTarget = () => _bluetoothOff ? BluetoothAvatar : Mascot;
        Greeting.HeaderOpacity += o => Header.Opacity = o;
        Greeting.IslandBounds = () =>
        {
            var r = _island.VisibleRect;
            return new Rect(Shell.TranslatePoint(r.TopLeft, Greeting), r.Size);
        };
        Greeting.Landing += Greeting_Landing;
        Greeting.Narrow += NarrowAfterIntro;
        Greeting.Finished += Greeting_Finished;
        SizeChanged += (_, e) =>
        {
            if (!e.HeightChanged || e.PreviousSize.Height <= 0) return;
            if (_settings.Docked) Dock(center: _dockCentered);     // docked: stay at the top
            else if (!_anchorTop) Top -= e.NewSize.Height - e.PreviousSize.Height;   // otherwise keep the bottom edge in place
        };
        MouseMove += (_, e) => ActiveMascot.LookAt(e.GetPosition(ActiveMascot));   // eyes follow the mouse
        MouseLeave += (_, _) => ActiveMascot.LookAhead();
        // The route view is as tall as the overview (its list scrolls) instead of growing the window.
        // The overview is top-aligned, so its height is its real height and the window can shrink again.
        HomeView.SizeChanged += (_, _) => RouteView.Height = HomeView.ActualHeight;
    }

    public string TrainerStatus => _trainer is { } t && _state.Current.Connected ? $"{t.Name} · connected" : _message;
    public bool BluetoothOff => _bluetoothOff;                     // same detection as the overlay

    // Trainer and strap count once each; control goes through the trainer.
    public int ConnectedDevices =>
        ((_trainer is not null || Demo) && _state.Current.Connected ? 1 : 0) + (_strap is not null && _state.Current.StrapConnected ? 1 : 0);

    public bool IsConnected(SavedDevice d) => d.Kind == "Trainer"
        ? _trainer is not null && _state.Current.Connected && _settings.TrainerAddress == d.Address
        : _strap is not null && _state.Current.StrapConnected && _settings.StrapAddress == d.Address;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(Native.Handle(this)).AddHook(WndProc);
        ApplyHotkeys();
    }

    // == Shortcuts ==

    // Registers all shortcuts with Windows. One taken by another app stays off, and the settings say so.
    public void ApplyHotkeys()
    {
        var hwnd = Native.Handle(this);
        if (hwnd == IntPtr.Zero) return;
        foreach (var (action, _, _) in Hotkeys.All)
        {
            Native.UnregisterHotKey(hwnd, (int)action);
            if (Hotkeys.Get(_settings, action) is not { } combo) { _hotkeyOk[action] = true; continue; }
            bool ok = Native.RegisterHotKey(hwnd, (int)action, combo.NativeModifiers | Hotkeys.Repeat(action), combo.VirtualKey);
            if (!ok && _hotkeyOk.GetValueOrDefault(action, true)) RawLog.Note($"Hotkey {combo} is used by another app");
            _hotkeyOk[action] = ok;
        }
        UpdateShortcutTexts();
    }

    // While a new shortcut is being recorded in the settings: unregister all, otherwise the key would trigger the action.
    public void SuspendHotkeys()
    {
        var hwnd = Native.Handle(this);
        foreach (var (action, _, _) in Hotkeys.All) Native.UnregisterHotKey(hwnd, (int)action);
    }

    public bool HotkeyWorks(HotkeyAction action) => Hotkeys.Get(_settings, action) is not null && _hotkeyOk.GetValueOrDefault(action, true);

    // "Settings" → "Settings (Ctrl+Alt+S)"; just the name without a shortcut.
    private string WithKeys(string text, HotkeyAction action) =>
        HotkeyWorks(action) ? $"{text} ({Hotkeys.Text(_settings, action)})" : text;

    // Tooltips show the current shortcuts.
    private void UpdateShortcutTexts()
    {
        ClickThroughButton.ToolTip = WithKeys("Click-through: clicks go to the video", HotkeyAction.ClickThrough);
        SettingsButton.ToolTip = WithKeys("Settings", HotkeyAction.Settings);
        MinimizeButton.ToolTip = WithKeys("Minimize", HotkeyAction.Compact);
        ExpandButton.ToolTip = WithKeys("Expand", HotkeyAction.Compact);
        MapOpener.ToolTip = WithKeys("Show map", HotkeyAction.Map);
        MapCloser.ToolTip = WithKeys("Hide map", HotkeyAction.Map);
        var lines = new[] { HotkeyAction.ErgUp, HotkeyAction.ErgDown, HotkeyAction.ErgToggle }
            .Where(HotkeyWorks).Select(a => $"{Hotkeys.Text(_settings, a)}   {Hotkeys.Label(a)}").ToArray();
        ErgPill.ToolTip = lines.Length > 0 ? string.Join("\n", lines) : "ERG: set shortcuts in Settings";
    }

    // Context menu: start or end ERG depending on the state, with the current shortcuts.
    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        string erg = _guard.Mode == ControlMode.Erg ? "End ERG" : "Start ERG";
        ErgMenuItem.Header = HotkeyWorks(HotkeyAction.ErgToggle) ? $"{erg}   {Hotkeys.Text(_settings, HotkeyAction.ErgToggle)}" : erg;
        SettingsMenuItem.Header = HotkeyWorks(HotkeyAction.Settings) ? $"Settings   {Hotkeys.Text(_settings, HotkeyAction.Settings)}" : "Settings";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RawLog.Note($"Domestique started {DateTime.Now:yyyy-MM-dd}");
        ApplySettings();                                                   // size and position before placing
        if (_settings.Docked && _settings.NotchDock && _settings.Left is double dockLeft) { Left = dockLeft; Dock(center: IsCentered()); }
        else if (_settings.Left is double left && _settings.Top is double top && IsOnScreen(left, top)) { Left = left; Top = top; SetDocked(false); }
        else PlaceDefault();
        // Start only after the first rendered frame: the first frame takes noticeably long (JIT, graphics) and the
        // animation would be over before anything is visible. Until then the window stays invisible.
        Root.Opacity = 0;
        _greeting = true;                                                  // notices and the Bluetooth card wait
        ContentRendered += (_, _) => StartGreeting();
        if (!Demo)
        {
            _radio = await BluetoothRadio.WatchAsync(on => Dispatcher.BeginInvoke(() => OnBluetoothChanged(on)));
            if (_radio is { State: not Windows.Devices.Radios.RadioState.On }) OnBluetoothChanged(false);   // already off at startup
        }
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);   // keep the PC awake while riding
        Native.KeepTopmost(this);                                           // stay above fullscreen video players
        if (RideRecorder.RecoverUnexported(RidesFolder).Count > 0) Notice(NoticeKind.Success, "Last ride recovered");
        Render(_state.Current);
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
        if (Demo) StartDemo();                                             // simulated trainer instead of Bluetooth
        else _ = KeepTrainerConnectedAsync();
    }

    // == Connections ==

    // Searches and connects until it works; the trainer may be switched on after the app.
    private async Task KeepTrainerConnectedAsync()
    {
        if (_trainerLoop) return;
        _trainerLoop = true;
        try
        {
            while (_trainer is null && !_closing)
            {
                if (await TryConnectTrainerAsync()) break;
                if (!_strapLoop) _ = KeepStrapConnectedAsync();             // strap only after the first trainer attempt
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10)), _wake.Task);   // Bluetooth on: continue right away
                _wake = new();
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
            _message = "Searching for trainer …";
            ulong? address = _settings.TrainerAddress
                ?? await BleScan.FindFirstAsync(BleUuids.FitnessMachineService, TimeSpan.FromSeconds(15));
            _bluetoothOff = false;                                          // the scan ran, so Bluetooth is on
            if (address is null) { _message = "No trainer found"; return false; }
            _message = "Connecting …";
            await trainer.ConnectAsync(address.Value);
            _trainer = trainer;
            _lastTrainerError = null;
            _settings.TrainerAddress = address;
            _settings.Remember(address.Value, trainer.Name, "Trainer");   // listed under "My devices"
            _settings.Save();
            if (trainer.PowerRange is { } range) _guard.PowerLimits = range;
            _guard.Reapply();                                               // start with a flat road or the target already chosen
            _message = "Waiting for trainer …";                               // shown only if the connection drops later
            return true;
        }
        catch (Exception ex)
        {
            await trainer.DisposeAsync();                                   // release the half-open connection
            if (ex.Message != _lastTrainerError) RawLog.Note("Trainer: " + ex.Message);   // don't repeat persistent errors every 10 s
            _lastTrainerError = ex.Message;
            _bluetoothOff = ex.Message.Contains("Bluetooth");
            _message = _bluetoothOff ? "Bluetooth is off" : "Connection failed";
            _settings.TrainerAddress = null;                                // search again next time
            _settings.Save();
            return false;
        }
        finally { _connectingTrainer = false; }
    }

    // Heart rate from a strap wins over the trainer's. A known strap is retried every 30 s (e.g. put on after launch).
    // Without a known strap there is only one scan, because continuous scanning can disturb the trainer connection.
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
            if (address is null) return false;                              // no strap nearby
            await strap.ConnectAsync(address.Value);
            _strap = strap;
            _settings.StrapAddress = address;
            _settings.Remember(address.Value, strap.Name, "Pulsgurt");
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            await strap.DisposeAsync();                                     // keep the address: the strap may just not be worn yet
            if (ex.Message != _lastStrapError) RawLog.Note("HR strap: " + ex.Message);
            _lastStrapError = ex.Message;
            return false;
        }
        finally { _connectingStrap = false; }
    }

    public async Task<bool> ReconnectTrainerAsync()
    {
        if (_trainer is not null) { await _trainer.DisposeAsync(); _trainer = null; }
        _message = "Reconnecting …";
        bool ok = await TryConnectTrainerAsync();                          // the mode guard sends the current target to the new trainer
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

    // == Tick: physics 4×/s, display 2×/s, grade, map and recording 1×/s ==

    private void OnTick()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds, 1);
        _lastTick = now;

        var s = _state.Current;
        bool stale = IsStale(s);
        _sim.Step(stale ? 0 : s.PowerW, dt);                                // no data: no stale power
        _state.Update(x => x with
        {
            VirtualSpeedKmh = _sim.SpeedMps * 3.6,
            DistanceM = _sim.DistanceM,
        });
        if (!Demo) Record(_state.Current, stale);                         // demo rides are not saved

        _ticks++;
        if (_ticks % 2 == 0) Render(_state.Current);
        if (_ticks % 4 != 0) return;
        // without a route the grade is 0, so after "No route" the trainer returns to flat smoothly
        _guard.SetGrade(_limiter.Next(_sim.GradePercent * _settings.Difficulty, 1));       // ignored in ERG
        MoveDots();
    }

    private static bool IsStale(SessionSnapshot s) => Stopwatch.GetElapsedTime(s.LastPacketTimestamp) > StaleAfter;

    // == Display (only the visible view is updated) ==

    private void Render(SessionSnapshot s)
    {
        bool stale = IsStale(s);
        bool controlLost = s.Connected && _trainer?.Control is { HasControl: false };
        int zone = stale ? 0 : PowerZones.Of(s.Power3sW, _settings.FtpW);
        DetectEvents(s, controlLost);
        if (!_greeting) SetOpaque(!s.Connected);
        // Asleep while no device is connected; only after 5 s without one, so short dropouts don't wake it and put it to sleep.
        if (s.Connected || s.StrapConnected) _deviceSeen = Environment.TickCount64;
        _asleep = _deviceSeen == 0 || Environment.TickCount64 - _deviceSeen > 5000;

        // Header: link dot and number of connected devices; the dot pops when something changes.
        if (SetFill(LinkDot, !s.Connected ? "MutedBrush" : stale || controlLost ? "WarningBrush" : "PositiveBrush")) Motion.PopIn(LinkDot, 0.3, 380);
        string count = ConnectedDevices.ToString();
        if (LinkCount.Text != count) { LinkCount.Text = count; Motion.Bounce(LinkButton, 0.85); }
        LinkButton.ToolTip = $"{TrainerStatus}\n{(s.StrapConnected ? "HR strap connected" : "No HR strap")}\nClick: Bluetooth settings";

        UpdateBluetoothCard();
        // by the chosen tab, not by visibility: the old view is still fading out after a switch
        if (_compact) RenderCompact(s, stale, controlLost, zone);
        else if (HomeTab.IsChecked == true) RenderHome(s, stale, controlLost, zone);
    }

    // 0 instead of a dash without trainer, data or sensor
    private static string PowerText(SessionSnapshot s, bool stale) => stale ? "0" : s.Power3sW.ToString();
    private static string HeartText(SessionSnapshot s) => (s.HeartRateBpm ?? 0).ToString();
    private static string CadenceText(SessionSnapshot s, bool stale) => stale || s.CadenceRpm is null ? "0" : $"{s.CadenceRpm:0}";

    // Face: asleep without a device (gray), otherwise relaxed, focused or at the limit by FTP zone (zone color).
    // Connected but no data right now (not pedaling): normal face.
    private Mood MoodFor(SessionSnapshot s, bool stale, int zone) =>
        _asleep ? Mood.Sleep
        : !s.Connected || stale ? Mood.Idle
        : zone >= 5 ? Mood.Strain
        : zone >= 3 ? Mood.Focus
        : Mood.Idle;

    // Badges: "…" while asleep and searching (Bluetooth on), "!" when another app has control.
    private void SetBadges(Avatar avatar, bool controlLost)
    {
        avatar.Busy = _asleep && !_bluetoothOff;
        avatar.Warning = controlLost;
    }

    private Avatar ActiveMascot => _compact ? MiniMascot : Mascot;

    private static Color? ZoneColor(int zone) => zone >= 2 ? ((SolidColorBrush)ZoneBrushes[zone]).Color : null;

    // Minimized: avatar and the chosen values in one row.
    private void RenderCompact(SessionSnapshot s, bool stale, bool controlLost, int zone)
    {
        MiniMascot.Mood = MoodFor(s, stale, zone);
        SetBadges(MiniMascot, controlLost);
        FillCompact(s, stale);
        if ((MiniMascot.Tag as int?) == zone) return;
        MiniMascot.Tag = zone;
        MiniMascot.Tint(ZoneColor(zone));
    }

    private void RenderHome(SessionSnapshot s, bool stale, bool controlLost, int zone)
    {
        Mascot.Mood = MoodFor(s, stale, zone);
        SetBadges(Mascot, controlLost);

        // Live values jump like in Zwift; counting up every value would redraw the overlay all the time (CPU).
        PowerRun.Text = PowerText(s, stale);
        CadenceRun.Text = CadenceText(s, stale);
        HeartRun.Text = HeartText(s);
        // ERG as in the design: on with the target in watts, otherwise "ERG OFF". The pill pops when the mode changes.
        string erg = _guard.Mode == ControlMode.Erg && _guard.TargetPowerW is int target ? $"ERG ON: {target}W" : "ERG OFF";
        if (ErgPillText.Text != erg)
        {
            bool modeChanged = ErgPillText.Text.StartsWith("ERG ON") != erg.StartsWith("ERG ON");
            ErgPillText.Text = erg;
            if (modeChanged) Motion.PopIn(ErgPill, 0.8, 420, 0, 0.5);
        }
        ShowZone(zone);

        // Top values as in the design: speed as integer, distance with one decimal, time without hours while possible
        SpeedRun.Text = $"{s.VirtualSpeedKmh:0}";
        DistanceRun.Text = $"{s.DistanceM / 1000:0.0}";
        ClimbRun.Text = $"{_sim.ClimbedM:0}";
        TimeRun.Text = Elapsed(_sim.MovingTime);                            // counts only while there is power
        RenderRoute();
    }

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    // Zone bar below the avatar: current zone in full Zwift color, the others faded. On a change the new segment pops
    // and from zone 2 the avatar takes on the zone color.
    private void ShowZone(int zone)
    {
        if ((ZoneBar.Tag as int?) == zone) return;
        ZoneBar.Tag = zone;
        for (int i = 0; i < 6; i++)
        {
            var segment = (Border)ZoneBar.Children[i];
            segment.Background = ZoneBrushes[i + 1];
            segment.Opacity = i + 1 == zone ? 1 : 0.3;                     // faded tones as in the design
            if (i + 1 != zone) continue;
            segment.RenderTransformOrigin = new Point(0.5, 0.5);
            Motion.Run(Motion.Scale(segment), ScaleTransform.ScaleYProperty, 1.9, 1, 420, Motion.Pop);
        }
        Mascot.Tint(ZoneColor(zone));
    }

    // Route progress bar in the HUD; ridden and total distance on the map card.
    private void RenderRoute()
    {
        if (_sim.Route is not { } r) { ProgressFill.Width = 0; return; }
        double done = Math.Min(_sim.RouteDistanceM, r.LengthM);
        double fill = Math.Round(done / r.LengthM * ((FrameworkElement)ProgressFill.Parent).ActualWidth, 1);
        if (ProgressFill.Width != fill) ProgressFill.Width = fill;
        RouteDistanceText.Text = $"{done / 1000:0.0} / {r.LengthM / 1000:0.0} KM";
    }

    // Sets a theme brush only when it changes, so nothing redraws needlessly.
    // true if the color changed (not on the first set).
    private static bool SetFill(System.Windows.Shapes.Shape shape, string key)
    {
        if ((string?)shape.Tag == key) return false;
        bool first = shape.Tag is null;
        shape.Tag = key;
        shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        return !first;
    }

    // == Bluetooth off ==

    // Windows reports switching on and off right away. On: card goes away and the search continues at once.
    private void OnBluetoothChanged(bool on)
    {
        _bluetoothOff = !on;
        _message = on ? "Searching for trainer …" : "Bluetooth is off";
        if (!on) { _bluetoothCardDismissed = false; Render(_state.Current); return; }
        _wake.TrySetResult();                                              // the trainer search doesn't wait the 10 s
        if (!_strapLoop && !Demo) _ = KeepStrapConnectedAsync();
        Render(_state.Current);
    }

    // "Bluetooth is off" is its own screen, not an overlay card: the view below is hidden and the tabs are locked.
    // The avatar glides from the overview into the card and back when Bluetooth comes on.
    // The card stays until tapped or Bluetooth is switched on.
    private void UpdateBluetoothCard()
    {
        bool show = _bluetoothOff && !_bluetoothCardDismissed;
        if (show == _bluetoothCardShown || _greeting) return;               // at launch the avatar glides in by itself
        bool fly = !_compact && HomeTab.IsChecked == true && !Greeting.Running;
        if (show) ShowBluetoothScreen(fly);
        else HideBluetoothScreen(fly);
    }

    private void ShowBluetoothScreen(bool fly)
    {
        _bluetoothCardShown = true;
        SetViewsLocked(true);
        BluetoothAvatar.Mood = Mood.Sleep;                                  // asleep (gray) without Bluetooth
        BluetoothCard.Visibility = Visibility.Visible;
        Motion.Run(BluetoothCard, OpacityProperty, 0, 1, 320, Co.OutE);
        // text only when the avatar has almost arrived, otherwise it would glide through the text at launch
        Motion.Reveal(BluetoothText.Children.Cast<UIElement>(), fly ? 300 : 420, 80, rise: true);
        Glow(BluetoothGlow);
        var view = CurrentView;
        if (!fly) { view.Visibility = Visibility.Hidden; return; }          // at launch: straight to the Bluetooth screen
        Greeting.Fly(Mascot, BluetoothAvatar, 560);
        // hide the view only after take-off: the avatar starts from its place in the overview
        Motion.Run(view, OpacityProperty, view.Opacity, 0, 260, Co.OutE, done: () =>
        {
            if (!_bluetoothCardShown) return;
            view.Visibility = Visibility.Hidden;
            view.Opacity = 1;
            Mascot.Opacity = 1;
        });
    }

    private void HideBluetoothScreen(bool fly)
    {
        _bluetoothCardShown = false;
        SetViewsLocked(false);
        var view = CurrentView;
        view.Visibility = Visibility.Visible;
        Motion.Run(view, OpacityProperty, 0, 1, 320, Co.OutE, 120);
        if (view == HomeView) Motion.Reveal(HudParts, 160, 55);
        if (fly) Greeting.Fly(BluetoothAvatar, Mascot, 560);
        Motion.Run(BluetoothCard, OpacityProperty, BluetoothCard.Opacity, 0, 300, Co.OutE, done: () =>
        {
            if (_bluetoothCardShown) return;
            BluetoothCard.Visibility = Visibility.Collapsed;
            BluetoothCard.Opacity = 1;
            BluetoothAvatar.Opacity = 1;
        });
        Render(_state.Current);
    }

    // Tabs locked while the Bluetooth screen is shown.
    private void SetViewsLocked(bool locked)
    {
        HomeTab.IsEnabled = RouteTab.IsEnabled = !locked;
        Motion.Run(TabBar, OpacityProperty, TabBar.Opacity, locked ? 0.35 : 1, 200, Co.OutE);
    }

    // Tapping opens the Bluetooth page of the Windows settings.
    private void BluetoothCard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // otherwise dragging starts
        _bluetoothCardDismissed = true;
        UpdateBluetoothCard();
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception ex) { RawLog.Note("Couldn't open Bluetooth settings: " + ex.Message); }
    }

    // == Notices ==

    // Every newly connected device shows the green check on the avatar. What a badge can't show (link lost mid-ride,
    // control lost) appears briefly in the header.
    private void DetectEvents(SessionSnapshot s, bool controlLost)
    {
        if (s.Connected != _wasConnected)
        {
            if (s.Connected) CheckMark();
            else if (_everConnected && !_closing) Notice(NoticeKind.Warning, "Trainer disconnected", 6);
            _everConnected |= s.Connected;
            _wasConnected = s.Connected;
        }
        bool hasControl = s.Connected && _trainer?.Control is { HasControl: true };
        if (controlLost && _hadControl) { _controlLost = true; Notice(NoticeKind.Warning, "Another app took control", 6); }
        if (hasControl && _controlLost) { _controlLost = false; CheckMark(); }
        _hadControl = hasControl;
        if (s.StrapConnected && !_strapWas) CheckMark();
        _strapWas = s.StrapConnected;
        // recording starts by itself with the first pedal stroke: say it once, small hop
        if (Recording && !_wasRecording) { Notice(NoticeKind.Info, "Recording started", 4); ActiveMascot.Hop(); }
        _wasRecording = Recording;
        if (_sim.Finished && !_wasFinished && _sim.Route is not null) { CheckMark(); Mascot.Greet(200); Notice(NoticeKind.Success, "Route finished"); }
        _wasFinished = _sim.Finished;
    }

    // Green check on both avatars (overview and minimized), whichever is visible.
    private void CheckMark()
    {
        Mascot.Done();
        MiniMascot.Done();
    }

    private enum NoticeKind { Info, Success, Warning, Error }

    // Short notice centered in the header; disappears by itself and waits during the launch.
    private void Notice(NoticeKind kind, string text, double seconds = 5)
    {
        if (_greeting) { _pendingNotice = (kind, text, seconds); return; }
        NoticeDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, kind switch
        {
            NoticeKind.Success => "PositiveBrush",
            NoticeKind.Warning => "WarningBrush",
            NoticeKind.Error => "NegativeBrush",
            _ => "AccentBrush",
        });
        NoticeText.Text = text;
        Motion.Rise(NoticeBar, 6, 380);
        _noticeTimer.Stop();
        _noticeTimer.Interval = TimeSpan.FromSeconds(seconds);
        _noticeTimer.Start();
    }

    // Glow at the bottom edge of the Bluetooth card.
    private static void Glow(System.Windows.Shapes.Shape glow)
    {
        glow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "NegativeBrush");
        Motion.Run(glow, OpacityProperty, 0, 0.5, 600);
    }
    // == Views ==

    // Hidden instead of Collapsed: hidden views keep their space, so the window size doesn't jump. On a switch the old
    // view dims briefly, the new one appears part by part with a slight overshoot, and the selection glides to the tab.
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomeView is null || RouteView is null) return;                 // during InitializeComponent
        FrameworkElement next = CurrentView;
        foreach (var view in new FrameworkElement[] { HomeView, RouteView })
        {
            if (view == next || view.Visibility != Visibility.Visible) continue;
            Motion.Run(view, OpacityProperty, view.Opacity, 0, 120, done: () =>
            {
                if (view == CurrentView) return;                           // selected again meanwhile
                view.Visibility = Visibility.Hidden;
                view.Opacity = 1;
            });
        }
        // Bluetooth screen shown (e.g. "Load route …" from the menu) or the route transition running:
        // only remember the view, it comes afterwards
        bool wait = _bluetoothCardShown || (_riding && next == HomeView);
        next.Visibility = wait ? Visibility.Hidden : Visibility.Visible;
        if (!wait) Motion.Run(next, OpacityProperty, next.Opacity, 1, 120);
        if (RouteTab.IsChecked == true) RefreshRouteList();
        Render(_state.Current);
        MoveDots();
        if (!wait) Motion.Reveal(RevealParts(next), 60, 55);
        if (next == RouteView) RevealRouteItems(160);

        var pill = Motion.Shift(TabPill);
        double x = (HomeTab.IsChecked == true ? 0 : 1) * TabStep;
        Motion.Run(pill, TranslateTransform.XProperty, pill.X, x, 380, Motion.Glide);
        TabPill.RenderTransformOrigin = new Point(0.5, 0.5);
        Motion.Keys(Motion.Scale(TabPill), ScaleTransform.ScaleXProperty, 0, [(0, 1), (110, 1.3), (400, 1)], Motion.Pop);   // stretches on the way
    }

    private FrameworkElement CurrentView => RouteTab.IsChecked == true ? RouteView : HomeView;

    // Parts of a view in the order they appear.
    private IEnumerable<UIElement> RevealParts(FrameworkElement view)
    {
        if (view == HomeView)
        {
            yield return RideCard;
            yield return MapCard;
        }
        else
        {
            yield return RouteBar;
            yield return AddPanel;
        }
    }

    // Overview values without the avatar and its card: while it lands, its place must not move.
    private UIElement[] HudParts => [.. TopStats.Children.Cast<UIElement>(), PowerColumn, PowerUnit, SideColumn, ErgPill, MapCard];

    // Route list items one after another.
    private void RevealRouteItems(int delayMs)
    {
        if (RouteList.Visibility != Visibility.Visible) return;
        RouteList.UpdateLayout();                                           // create the items
        var items = Enumerable.Range(0, Math.Min(RouteList.Items.Count, 12))
            .Select(i => RouteList.ItemContainerGenerator.ContainerFromIndex(i)).OfType<UIElement>();
        Motion.Reveal(items, delayMs, 35, rise: true);
    }

    // == Recording ==

    // Speed and distance come from the physics model, not from the trainer.
    private void Record(SessionSnapshot s, bool stale) =>
        _ride.Tick(Stopwatch.GetElapsedTime(AppStart), DateTime.UtcNow, stale ? 0 : s.PowerW, stale ? 0 : s.CadenceRpm ?? 0,
                   s.HeartRateBpm, _sim.SpeedMps, s.DistanceM);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_ride.Export() is { } file) Notice(NoticeKind.Success, "Ride saved · Documents\\Domestique", 6);
            else Notice(NoticeKind.Info, "No ride yet");
        }
        catch (Exception ex) { Notice(NoticeKind.Error, "Export failed: " + ex.Message, 8); }
    }

    // == Routes and map ==

    // A route list entry. Key: file, "app:Name" or null for "No route".
    public sealed record RouteItem(string? Key, string Label, string Icon, string Detail);

    private const string NoRouteIcon = "", RouteIcon = "";

    // Context menu "Load route …": opens the route view on "Add".
    private void LoadRoute_Click(object sender, RoutedEventArgs e)
    {
        RouteTab.IsChecked = true;
        AddSegment.IsChecked = true;
    }

    private void RouteMode_Checked(object sender, RoutedEventArgs e)
    {
        if (AddPanel is null || RouteList is null) return;                 // during InitializeComponent
        bool add = AddSegment.IsChecked == true;
        AddPanel.Visibility = add ? Visibility.Visible : Visibility.Collapsed;
        RouteList.Visibility = ConfirmRouteButton.Visibility = add ? Visibility.Collapsed : Visibility.Visible;
        SourceBar.Visibility = add ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RouteSource_Checked(object sender, RoutedEventArgs e) => RefreshRouteList();

    // "No route" is always first, then own or built-in routes; the active one is preselected.
    private void RefreshRouteList(string? select = null)
    {
        if (RouteList is null) return;
        var items = new List<RouteItem> { new(null, "No route", NoRouteIcon, "flat") };
        if (AppRoutesSegment.IsChecked == true)
            items.AddRange(BuiltInRoutes.Names.Select(n => new RouteItem("app:" + n, n, RouteIcon, $"{BuiltInRoutes.LengthKm(n):0} km")));
        else
            items.AddRange(_library.Routes.Select(f => new RouteItem(f, Path.GetFileNameWithoutExtension(f), RouteIcon, "")));
        RouteList.ItemsSource = items;
        string? wanted = select ?? _activeRoute;
        RouteList.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Key, wanted, StringComparison.OrdinalIgnoreCase)) ?? items[0];
    }

    private void PickRouteFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GPX route (*.gpx)|*.gpx" };
        if (dialog.ShowDialog(this) == true) ImportRoute(dialog.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            foreach (var file in files) ImportRoute(file);
    }

    // A new file goes to the library and is selected there; it is used only after "Use route".
    private void ImportRoute(string path)
    {
        try
        {
            string imported = _library.Import(path);
            RouteTab.IsChecked = true;
            OwnRoutesSegment.IsChecked = true;
            LibrarySegment.IsChecked = true;
            RefreshRouteList(select: imported);
            RouteList.ScrollIntoView(RouteList.SelectedItem);
        }
        catch (Exception ex) { Notice(NoticeKind.Error, "Can't read file: " + ex.Message, 8); }
    }

    private void ConfirmRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteList.SelectedItem is not RouteItem item) return;
        if (item.Key is null) UnloadRoute();
        else if (item.Key.StartsWith("app:")) ShowRoute(BuiltInRoutes.Load(item.Key[4..]), item.Key);
        else LoadFile(item.Key);
    }

    public void LoadFile(string path)
    {
        try { ShowRoute(GpxLoader.Load(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path)), path); }
        catch (Exception ex) { Notice(NoticeKind.Error, "Can't read route: " + ex.Message, 8); }
    }

    // Shows the route on the overview, with the transition to it.
    private void ShowRoute(Route route, string key)
    {
        bool fromHome = HomeTab.IsChecked == true && HomeView.Visibility == Visibility.Visible;
        _sim.Load(route);
        _activeRoute = key;
        MapLine.Points = RouteGeometry.Map(route, 102, 102, out _mapProject);
        Profile.SetRoute(route);
        RouteNameText.Text = route.Name.ToUpperInvariant();
        PlayRouteLoaded(fromHome);
        Render(_state.Current);
        MoveDots();
        RawLog.Note($"Route loaded: {route.Name}, {route.LengthM / 1000:0.0} km");
    }

    private void UnloadRoute()
    {
        _sim.Unload();
        _activeRoute = null;
        _mapProject = null;
        Profile.SetRoute(null);
        SetMapOpen(false);
        HomeTab.IsChecked = true;
        Render(_state.Current);
    }

    private void MoveDots()
    {
        if (_sim.Route is not { } route || _mapProject is null || MapCard.Visibility != Visibility.Visible) return;
        double distance = Math.Min(_sim.RouteDistanceM, route.LengthM);
        var m = _mapProject(route.At(distance));
        Canvas.SetLeft(MapDot, m.X - 4);
        Canvas.SetTop(MapDot, m.Y - 4);
        MapDone.Points = RouteGeometry.Done(MapLine.Points, route, distance, _mapProject);   // ridden part highlighted
        Profile.SetPosition(distance, _sim.GradePercent);
    }

    // == Map expand and collapse ==

    private double ExpandedWidth => HudW + (_mapOpen ? MapW : 0);

    // The notch springs wider on both sides (so it stays centered) while the map fades in.
    // Collapsing fades the map out first, then the notch closes without overshoot.
    private void SetMapOpen(bool open, bool animate = true)
    {
        open &= _sim.Route is not null;
        MapOpener.Visibility = _sim.Route is not null && !open ? Visibility.Visible : Visibility.Collapsed;
        if (open == _mapOpen) return;
        _mapOpen = open;
        if (_compact || _switching || _greeting || !animate || !Motion.Enabled)
        {
            MapCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            MapCard.Opacity = 1;
            if (!_compact) { _widthLoop.Stop(); Shell.Width = ExpandedWidth; }   // minimized: Expand sets the width
            MoveDots();
            return;
        }
        if (open)
        {
            MapCard.Visibility = Visibility.Visible;
            MoveDots();
            Motion.Run(MapCard, OpacityProperty, 0, 1, 260, Co.OutE, 120);
            AnimateWidth(ExpandedWidth, 0.86);
        }
        else
        {
            Motion.Run(MapCard, OpacityProperty, MapCard.Opacity, 0, 140, Co.OutE, done: () =>
            {
                if (_mapOpen) return;                                       // expanded again meanwhile
                MapCard.Visibility = Visibility.Collapsed;
                MapCard.Opacity = 1;
            });
            Motion.After(80, () => { if (!_mapOpen) AnimateWidth(ExpandedWidth, 1); });
        }
    }

    private void AnimateWidth(double target, double damping)
    {
        if (!_shellWidth.Animating) _shellWidth.Jump(Shell.Width);
        _shellWidth.SpringTo(target, 0.42, damping);
        _widthLoop.Start();
    }

    // Not a button: the right edge lights up slightly on hover, a click expands or collapses the map.
    private void MapEdge_MouseEnter(object sender, MouseEventArgs e) => LightEdge((Border)sender, 1);

    private void MapEdge_MouseLeave(object sender, MouseEventArgs e) => LightEdge((Border)sender, 0);

    private void MapEdge_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // otherwise dragging starts
        LightEdge((Border)sender, 0);
        _settings.ShowMap = !_mapOpen;                                      // also applies to the next route
        SetMapOpen(!_mapOpen);
    }

    private static void LightEdge(Border edge, double to)
    {
        var glow = (UIElement)edge.Child;
        Motion.Run(glow, OpacityProperty, glow.Opacity, to, to > 0 ? 160 : 260, Co.OutE);
    }

    // == Settings ==

    public void ApplySettings()
    {
        var surface = (Color)FindResource("SurfaceColor");
        surface.A = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, 0.2, 1) * 255);   // fully transparent would be unreadable
        _surface = surface;
        ApplyBackground();

        double scale = Math.Clamp(_settings.Scale, 0.5, 2);
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        Width = BaseWidth * scale;
        SetMapOpen(_settings.ShowMap);
        ApplyCompactValues();
        if (!_settings.NotchDock && _settings.Docked) { SetDocked(false); Top += 12; }
        else SetDocked(_settings.Docked);                                  // corners match the dock state

        _sim.Physics.MassKg = _settings.RiderKg + _settings.BikeKg;
        _sim.Physics.CdA = Math.Clamp(_settings.CdA, 0.1, 1);
        Render(_state.Current);
    }

    // Which values the small notch shows. If it is minimized right now, its width follows smoothly.
    private void ApplyCompactValues()
    {
        foreach (FrameworkElement slot in CompactValues.Children)
            slot.Visibility = _settings.CompactValues.Contains((string)slot.Tag) ? Visibility.Visible : Visibility.Collapsed;
        if (_compact && !_switching && Math.Abs(Shell.Width - CompactWidth) > 0.5) AnimateWidth(CompactWidth, 0.86);
    }

    private void FillCompact(SessionSnapshot s, bool stale)
    {
        MiniPowerRun.Text = PowerText(s, stale);
        MiniHeartRun.Text = HeartText(s);
        MiniCadenceRun.Text = CadenceText(s, stale);
        MiniSpeedRun.Text = $"{s.VirtualSpeedKmh:0}";
        MiniDistanceRun.Text = $"{s.DistanceM / 1000:0.0}";
        MiniClimbRun.Text = $"{_sim.ClimbedM:0}";
        MiniTimeRun.Text = Elapsed(_sim.MovingTime);
        FitCompactSlots();
    }

    // Distance, climb and time only grow: their slot is as wide as the value. When a digit is added (rarely) the small
    // notch grows smoothly. Digits are equally wide, so the width only changes with the digit count.
    private void FitCompactSlots()
    {
        bool changed = false;
        foreach (var (slot, run, icon) in new[] { (DistanceSlot, MiniDistanceRun, MiniDistanceIcon), (ClimbSlot, MiniClimbRun, MiniClimbIcon),
                                                  (TimeSlot, MiniTimeRun, MiniTimeIcon) })
        {
            var text = new FormattedText(run.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(TryFindResource("HudFont") as FontFamily ?? FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                21, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double width = Math.Ceiling(text.WidthIncludingTrailingWhitespace + icon.Margin.Left + icon.Width);
            if (Math.Abs(slot.Width - width) < 0.5) continue;
            slot.Width = width;
            changed |= slot.Visibility == Visibility.Visible;
        }
        if (changed && _compact && !_switching) AnimateWidth(CompactWidth, 0.86);
    }

    // Small notch width: border, padding, avatar, the visible slots with spacing, expand button.
    private double CompactWidth =>
        2 + 8 + MiniMascot.Width + MiniMascot.Margin.Right
        + CompactValues.Children.OfType<FrameworkElement>().Where(s => s.Visibility == Visibility.Visible).Sum(s => s.Width + s.Margin.Right)
        + 32 + 4;

    // Background of panel and notch ears: fully opaque until a trainer is connected, then the chosen opacity.
    private Color ShellColor
    {
        get
        {
            var c = _surface;
            if (_opaque) c.A = 255;
            return c;
        }
    }

    private void ApplyBackground()
    {
        Shell.Background = LeftEar.Fill = RightEar.Fill = Frozen(ShellColor);
        var solid = _surface;
        solid.A = 255;
        NoticeBar.Background = Frozen(solid);                               // the notice covers what lies below it in the header
    }

    // cross-fades when the connection state changes
    private void SetOpaque(bool opaque)
    {
        if (_opaque == opaque) return;
        var from = ShellColor;
        _opaque = opaque;
        if (!Motion.Enabled) { ApplyBackground(); return; }
        var brush = new SolidColorBrush(from);
        Shell.Background = LeftEar.Fill = RightEar.Fill = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(ShellColor, TimeSpan.FromMilliseconds(600)) { EasingFunction = Co.InOutE });
        Motion.After(650, () => { if (Shell.Background == brush) ApplyBackground(); });
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OpenSettings(bool bluetooth = false)
    {
        if (_clickThrough) SetClickThrough(false);
        if (_settingsWindow is { IsLoaded: true })
        {
            if (bluetooth) _settingsWindow.ShowBluetooth();
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, _settings, _library);
        if (bluetooth) _settingsWindow.ShowBluetooth();
        // Opens next to the overlay like a popover: below if there is room, otherwise above. Both windows have a
        // transparent margin for their springs, so the visible areas are used.
        var screen = Native.MonitorBounds(this);
        double padX = SettingsWindow.PadX, padY = SettingsWindow.PadY;
        double width = _settingsWindow.Width - 2 * padX, height = _settingsWindow.Height - 2 * padY;
        double shellRight = Shell.TranslatePoint(new Point(Shell.ActualWidth, 0), this).X;   // right edge of the notch, not of the window
        double left = Math.Clamp(Left + shellRight - width, screen.Left + 8, screen.Right - width - 8);
        double below = Top + ActualHeight - Overhang + 8, above = Top - height - 8;
        _settingsWindow.Left = left - padX;
        _settingsWindow.Top = (below + height <= screen.Bottom ? below : Math.Max(screen.Top + 8, above)) - padY;
        _settingsWindow.Show();
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void LinkButton_Click(object sender, RoutedEventArgs e) => OpenSettings(bluetooth: true);

    // == Position and notch ==

    // Drag and release: near the top edge the overlay docks, near the center it snaps to the middle.
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Greeting.Running) Greeting.Skip();                             // click during the launch: go to the place right away
        if (_switching) return;
        var before = new Point(Left, Top);
        bool wasDocked = _settings.Docked;
        DragMove();
        if (!_settings.NotchDock || before == new Point(Left, Top)) return;    // only a click, not moved
        var screen = Native.MonitorBounds(this);
        if (Top - screen.Top < SnapDistance) { DockAnimated(center: Math.Abs(Left + ActualWidth / 2 - screen.Left - screen.Width / 2) < CenterSnap); return; }
        SetDocked(false);
        if (wasDocked) Motion.Bounce(Shell, 0.97, 380);                    // detaches with a short bounce
        _settings.Left = Left;
        _settings.Top = Top;
    }

    // Docking like a drop merging with the edge: glides up (and to the center), the top corners flatten and the
    // notch ears flow out of the edges. No overshoot.
    private void DockAnimated(bool center)
    {
        var screen = Native.MonitorBounds(this);
        double left = center ? screen.Left + (screen.Width - ActualWidth) / 2 : Left;
        bool wasDocked = _settings.Docked;
        _settings.Left = left;
        _settings.Top = screen.Top;
        if (center) Motion.Run(this, LeftProperty, Left, left, 260, Co.InOutE);
        Motion.Run(this, TopProperty, Top, screen.Top, 260, Co.InOutE, done: () =>
        {
            Dock(center);
            if (!wasDocked) Merge();
        });
    }

    private void Merge()
    {
        var r = (CornerRadius)FindResource("ShellRadius");
        LeftEar.RenderTransformOrigin = new Point(1, 0);                   // grow from the corner at the panel
        RightEar.RenderTransformOrigin = new Point(0, 0);
        var shell = Motion.Scale(Shell);
        Shell.RenderTransformOrigin = new Point(0.5, 0);
        Tween.Run(420, Co.InOut, k =>
        {
            double top = r.TopLeft * (1 - k);
            Shell.CornerRadius = new CornerRadius(top, top, r.BottomRight, r.BottomLeft);
            double ear = Co.Out(k);
            Motion.Scale(LeftEar).ScaleX = Motion.Scale(LeftEar).ScaleY = ear;
            Motion.Scale(RightEar).ScaleX = Motion.Scale(RightEar).ScaleY = ear;
            shell.ScaleY = 1 + 0.025 * Math.Sin(Math.PI * k);                // pulls towards the edge briefly
        });
    }

    private void Dock(bool center)
    {
        var screen = Native.MonitorBounds(this);
        Top = screen.Top;
        if (center) Left = screen.Left + (screen.Width - ActualWidth) / 2;
        _dockCentered = center;
        SetDocked(true);
    }

    private bool IsCentered()
    {
        var screen = Native.MonitorBounds(this);
        return Math.Abs(Left + ActualWidth / 2 - screen.Left - screen.Width / 2) < 2;
    }

    // Docked: square top without border, with the curved notch ears. Floating: rounded all around.
    private void SetDocked(bool docked)
    {
        _settings.Docked = docked;
        _island.Docked = docked;
        var r = (CornerRadius)FindResource("ShellRadius");
        Shell.CornerRadius = docked ? new CornerRadius(0, 0, r.BottomRight, r.BottomLeft) : r;
        Shell.BorderThickness = docked ? new Thickness(0) : new Thickness(1);
        LeftEar.Visibility = RightEar.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ResetPosition()
    {
        _settings.Left = _settings.Top = null;
        PlaceDefault();
    }

    // Notch at the top center, otherwise bottom right.
    private void PlaceDefault()
    {
        UpdateLayout();
        if (_settings.NotchDock) { Dock(center: true); return; }
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight + Overhang - 16;
        SetDocked(false);
    }

    // A saved position on a monitor that was unplugged: place it again.
    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 50
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    // == Shortcuts, buttons and menu ==

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY) { OnHotkey(wParam.ToInt32()); handled = true; }
        return IntPtr.Zero;
    }

    private void OnHotkey(int id)
    {
        switch ((HotkeyAction)id)
        {
            case HotkeyAction.ErgUp: PowerStep(+10); break;
            case HotkeyAction.ErgDown: PowerStep(-10); break;
            case HotkeyAction.ErgToggle: ToggleErg(); break;
            case HotkeyAction.ClickThrough: SetClickThrough(!_clickThrough); break;
            case HotkeyAction.Compact: if (_compact) Expand_Click(this, new RoutedEventArgs()); else Minimize_Click(this, new RoutedEventArgs()); break;
            case HotkeyAction.Map: ToggleMap(); break;
            case HotkeyAction.Settings: OpenSettings(); break;
        }
    }

    // Map via shortcut, same as clicking the edge; without a route there is none.
    private void ToggleMap()
    {
        if (_sim.Route is null) { Notice(NoticeKind.Info, "No route loaded", 3); return; }
        _settings.ShowMap = !_mapOpen;
        SetMapOpen(!_mapOpen);
    }

    private void PowerStep(int watts)
    {
        _guard.SetErg((_guard.TargetPowerW ?? 150) + watts);
        Render(_state.Current);                                             // show right away, not on the next tick
    }

    // Back to simulation with the grade the trainer should have now.
    private void EndErg()
    {
        _guard.EndErg(_sim.Route is null ? 0 : _limiter.Current);
        Render(_state.Current);
    }

    // ERG on (with the last target, otherwise 150 W) or off.
    private void ToggleErg()
    {
        if (_guard.Mode == ControlMode.Erg) EndErg();
        else PowerStep(0);
    }

    private void ErgToggle_Click(object sender, RoutedEventArgs e) => ToggleErg();

    // Click-through only with a working shortcut: clicks then pass through the overlay and the shortcut is the only way back.
    private void SetClickThrough(bool on)
    {
        if (on && !HotkeyWorks(HotkeyAction.ClickThrough))
        {
            ClickThroughButton.IsChecked = false;
            Notice(NoticeKind.Warning, "Set a click-through shortcut first", 6);
            return;
        }
        _clickThrough = on;
        Native.SetClickThrough(this, on);
        ClickThroughButton.IsChecked = on;
        if (on) Notice(NoticeKind.Info, $"Click-through on · {Hotkeys.Text(_settings, HotkeyAction.ClickThrough)} to undo", 6);
    }

    private void ClickThrough_Click(object sender, RoutedEventArgs e) => SetClickThrough(ClickThroughButton.IsChecked == true);

    // Quit from the menu: collapse first, then close.
    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting) return;
        _exiting = true;
        PlayClose(Close);
    }

    // == Avatar: hover and tap ==

    private void Mascot_MouseEnter(object sender, MouseEventArgs e) => ((Avatar)sender).Hover(true);

    private void Mascot_MouseLeave(object sender, MouseEventArgs e) => ((Avatar)sender).Hover(false);

    // Slap like in Coucou: squashes, springs back, squints. Three slaps within 1.7 s are too many: it spins dizzily
    // and is back to normal afterwards.
    private void Mascot_Poke(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                   // otherwise dragging starts
        var avatar = (Avatar)sender;
        long now = Environment.TickCount64;
        _pokes.RemoveAll(t => now - t > 1700);
        _pokes.Add(now);
        avatar.Slap();
        if (_pokes.Count < 3) return;
        _pokes.Clear();
        avatar.Dizzy();
    }

    // == Route loaded ==

    // Transition instead of a loading animation, right when the route is confirmed: the route view (or overview) fades
    // out, the avatar rides a straight line with a few stones and lands on its place while the overview appears.
    // Then the map expands (if enabled) and the green check shows. Minimized, on the Bluetooth screen or without
    // animations: straight to the overview.
    private void PlayRouteLoaded(bool fromHome)
    {
        void Finish()
        {
            SetMapOpen(_settings.ShowMap);
            CheckMark();
        }
        if (_compact || _bluetoothCardShown || _greeting || !Motion.Enabled || Ride.Running)
        {
            HomeTab.IsChecked = true;
            Finish();
            return;
        }
        _riding = true;
        if (fromHome) Motion.Run(HomeView, OpacityProperty, HomeView.Opacity, 0, 160, Co.OutE);
        HomeTab.IsChecked = true;                                           // the selection glides already, the overview waits
        UpdateLayout();
        var area = new Rect(ViewsArea.TranslatePoint(new Point(0, 0), Ride), new Size(ViewsArea.ActualWidth, ViewsArea.ActualHeight));
        Ride.Start(fromHome ? Mascot : null, Mascot, area, () =>
        {
            _riding = false;
            Finish();
        });
    }

    // The avatar glides to its place: fade in the overview, values appear one after another.
    private void Ride_Landing()
    {
        _riding = false;
        if (CurrentView != HomeView || _bluetoothCardShown) return;          // elsewhere meanwhile
        HomeView.Visibility = Visibility.Visible;
        Motion.Run(HomeView, OpacityProperty, 0, 1, 300, Co.OutE);
        Motion.Reveal(HudParts, 60, 45);
        Render(_state.Current);
    }
    // == Launch, minimize, quit ==

    // Launch (sequence from Coucou): the small notch springs open, fully opaque. GreetingView draws the card,
    // particles and the avatar; at the end the avatar glides to its place.
    private void StartGreeting()
    {
        Root.Opacity = 1;
        UpdateLayout();
        if (!Motion.Enabled) { Greeting_Finished(); return; }
        Shell.Width = IntroW;                                               // wide for the intro, narrows before the landing
        UpdateLayout();
        Views.Opacity = 0;
        Header.Opacity = 0;
        Mascot.Opacity = 0;
        ApplyBackground();                                                  // fully opaque without a trainer
        _island.Docked = _settings.Docked;
        _island.Jump(StartW, CompactH, CompactR);
        _island.Open();
        Greeting.Prepare();                                                 // lay out first, then measure
        var area = ViewsArea.TranslatePoint(new Point(0, 0), Greeting);
        Greeting.Start(new Rect(area, new Size(ViewsArea.ActualWidth, ViewsArea.ActualHeight)), StartW, CompactH);
    }

    // Only the width changes: the avatar stays centered while the notch closes in to the HUD.
    private void NarrowAfterIntro(bool instant)
    {
        if (instant)
        {
            _widthLoop.Stop();
            Shell.Width = ExpandedWidth;
            return;
        }
        _shellWidth.Jump(Shell.Width);
        _shellWidth.CurveTo(ExpandedWidth, 420, _widthLoop.Now);
        _widthLoop.Start();
    }

    // The avatar glides to its place: into the overview or into the Bluetooth card. The content appears one after another.
    private void Greeting_Landing(Avatar target)
    {
        target.Opacity = 0;
        Header.Opacity = 1;
        Motion.Run(Views, OpacityProperty, 0, 1, 260, Co.OutE);
        if (target == BluetoothAvatar) ShowBluetoothScreen(fly: false);
        else Motion.Reveal(HudParts, 140, 60);
    }

    private void Greeting_Finished()
    {
        _greeting = false;
        Views.Opacity = Header.Opacity = 1;
        Mascot.Opacity = BluetoothAvatar.Opacity = 1;                       // both visible again, wherever it landed
        Render(_state.Current);                                             // Bluetooth may have changed meanwhile
        if (_pendingNotice is { } n) { _pendingNotice = null; Notice(n.Kind, n.Text, n.Seconds); }
    }

    // Minimize via button or shortcut: content fades, the island closes to the small notch with the close curve,
    // then the avatar and the chosen values appear. Never automatic.
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_compact || _switching || _greeting) return;
        _switching = _anchorTop = true;
        _widthLoop.Stop();                                                  // the map may be animating: the width is fixed from now on
        FillCompact(_state.Current, IsStale(_state.Current));               // slots matching the values before the width is fixed
        // The window keeps its full height (transparent below the pill, clicks pass through): when a layered window resizes,
        // Windows briefly shows the old image at the new size, which flickered on expand.
        MinHeight = ActualHeight;
        Motion.Run(Expanded, OpacityProperty, 1, 0, 110, Co.OutE);         // fade out first, then close,
        Motion.After(100, () => _island.Close(CompactWidth, CompactH, CompactR, () =>   // otherwise the edge cuts the content
        {
            _compact = true;
            Expanded.Visibility = Visibility.Collapsed;
            Expanded.Opacity = 1;
            CompactBar.Visibility = Visibility.Visible;
            Shell.Width = CompactWidth;
            Shell.Padding = new Thickness(8, 0, 4, 0);
            UpdateLayout();
            _island.Reset();
            Render(_state.Current);
            Motion.PopIn(MiniMascot, 0.4, 420, 0.5, 0.6);
            Motion.Reveal([.. CompactValues.Children.Cast<UIElement>(), ExpandButton], 60, 35);
            _switching = false;
        }));
    }

    // Expand: the small notch springs open again (as at launch), the content appears one after another.
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (!_compact || _switching) return;
        _switching = true;
        Motion.Run(CompactBar, OpacityProperty, 1, 0, 100, Co.OutE, done: () =>
        {
            _compact = false;
            CompactBar.Visibility = Visibility.Collapsed;
            CompactBar.Opacity = 1;
            Shell.Width = ExpandedWidth;
            Shell.Padding = new Thickness(10, 7, 10, 10);
            Expanded.Visibility = Visibility.Visible;
            Header.Opacity = Views.Opacity = 0;
            UpdateLayout();
            _island.Jump(CompactWidth, CompactH, CompactR);
            _island.Open(() => { _switching = false; _anchorTop = false; MinHeight = 0; });
            Render(_state.Current);
            // content only when the island is almost open, otherwise its edge cuts the cards
            Motion.Run(Header, OpacityProperty, 0, 1, 220, Co.OutE, 240);
            Motion.Run(Views, OpacityProperty, 0, 1, 220, Co.OutE, 260);
            Motion.Reveal(_bluetoothCardShown ? [BluetoothCard] : RevealParts(CurrentView), 280, 55);
        });
    }

    // Quit: content fades, the avatar closes its eyes, the island closes to the small notch
    // and then retracts into the top edge.
    private void PlayClose(Action done)
    {
        ActiveMascot.Flash(Mood.Content, 3000);
        Motion.Run(_compact ? CompactBar : Expanded, OpacityProperty, 1, 0, 140, Co.OutE);
        void Retract() => _island.Close(NotchW, 0, CompactR, done, 300);
        if (_compact) Retract();
        else _island.Close(StartW, CompactH, CompactR, () => Motion.After(90, Retract));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _timer.Stop();                                                      // no new recording after the export
        var hwnd = Native.Handle(this);
        foreach (var (action, _, _) in Hotkeys.All) Native.UnregisterHotKey(hwnd, (int)action);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        try { _ride.Export(); }
        catch (Exception ex) { RawLog.Note("Export on quit failed: " + ex.Message); }   // the CSV stays and is recovered on the next start
        _settingsWindow?.Close();
        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_trainer is not null) await _trainer.DisposeAsync();            // otherwise the trainer stays busy for other apps
        if (_strap is not null) await _strap.DisposeAsync();
        base.OnClosed(e);
    }
}

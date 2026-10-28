using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Domestique.Ble;
using Domestique.Core.Library;
using Domestique.Core.Protocol;

namespace Domestique.App;

public sealed record DeviceItem(ulong Address, string Name, string Detail);
public sealed record LibraryItem(string FilePath, string Label);

// Settings popover. Every change applies immediately; invalid input is marked red and not applied.
// Saving happens shortly after the last change so a dragged slider doesn't write on every pixel.
public partial class SettingsWindow : Window
{
    public const double PadX = 14, PadY = 36;                                // transparent margin around the panel (Popover.Margin)
    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private readonly LibraryStore _library;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool _loading = true;
    private bool _closingAnimated;

    public SettingsWindow(MainWindow main, AppSettings settings, LibraryStore library)
    {
        InitializeComponent();
        _main = main;
        _s = settings;
        _library = library;
        if (main.IsVisible) Owner = main;                                     // stays above the overlay and closes with it
        _save.Tick += (_, _) => { _save.Stop(); _s.Save(); };
        _status.Tick += (_, _) => RefreshStatus();

        OpacitySlider.Value = _s.BackgroundOpacity * 100;
        ScaleSlider.Value = _s.Scale;
        NotchBox.IsChecked = _s.NotchDock;
        ShowMapBox.IsChecked = _s.ShowMap;
        FtpBox.Text = _s.FtpW.ToString();
        RiderBox.Text = _s.RiderKg.ToString();
        BikeBox.Text = _s.BikeKg.ToString();
        CdaBox.Text = _s.CdA.ToString();
        DifficultySlider.Value = _s.Difficulty * 100;
        foreach (CheckBox chip in CompactChips.Children) chip.IsChecked = _s.CompactValues.Contains((string)chip.Tag);
        HotkeyList.ItemsSource = _hotkeys;
        RefreshHotkeys();
        UpdateValueLabels();
        RefreshLibrary();
        RefreshMyDevices();
        HeaderAvatar.Mood = Mood.Idle;
        HeaderAvatar.Gear = true;                                             // gray gear badge
        MouseMove += (_, e) => HeaderAvatar.LookAt(e.GetPosition(HeaderAvatar));   // eyes follow the mouse
        MouseLeave += (_, _) => HeaderAvatar.LookAhead();
        _loading = false;
        Popover.Opacity = 0;                                                  // invisible until the first frame, then it pops open
        ContentRendered += (_, _) => PlayOpen();
        Closed += (_, _) =>
        {
            _status.Stop();
            if (_recording is not null) _main.ApplyHotkeys();                 // closed while recording a shortcut: turn the shortcuts back on
            if (_save.IsEnabled) { _save.Stop(); _s.Save(); }
        };
    }

    // Opens like a popover from the gear: slight overshoot, cards appear one after another, the avatar greets.
    private void PlayOpen()
    {
        MovePill(animate: false);
        bool below = Owner is null || Top >= Owner.Top;
        Motion.PopIn(Popover, 0.9, 460, 0.9, below ? 0 : 1);
        Motion.Reveal(PageItems(CurrentPage), 140, 35, rise: true);
        HeaderAvatar.Greet(260);
    }

    private void ScheduleSave()
    {
        _save.Stop();
        _save.Start();
    }

    // == Tabs ==

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (LookPage is null) return;                                         // during InitializeComponent
        LookPage.Visibility = Show(LookTab.IsChecked == true);
        RiderPage.Visibility = Show(RiderTab.IsChecked == true);
        BluetoothPage.Visibility = Show(BluetoothTab.IsChecked == true);
        LibraryPage.Visibility = Show(LibraryTab.IsChecked == true);
        KeysPage.Visibility = Show(KeysTab.IsChecked == true);
        if (BluetoothTab.IsChecked == true) { RefreshStatus(); _status.Start(); } else _status.Stop();
        if (KeysTab.IsChecked == true) RefreshHotkeys();                      // e.g. taken by another app meanwhile
        if (!IsLoaded) return;                                                // opening shows the page anyway
        Motion.Reveal(PageItems(CurrentPage), 30, 30, rise: true);
        MovePill(animate: true);
    }

    private FrameworkElement CurrentPage =>
        RiderTab.IsChecked == true ? RiderPage : BluetoothTab.IsChecked == true ? BluetoothPage : LibraryTab.IsChecked == true ? LibraryPage
        : KeysTab.IsChecked == true ? KeysPage : LookPage;

    // Cards of a page in the order they appear.
    private static IEnumerable<UIElement> PageItems(FrameworkElement page) => page switch
    {
        ScrollViewer { Content: Panel panel } => panel.Children.Cast<UIElement>(),
        Panel panel => panel.Children.Cast<UIElement>().SelectMany(c => c is StackPanel s ? s.Children.Cast<UIElement>() : new[] { c }),
        _ => [page],
    };

    // Glides under the chosen tab and stretches on the way.
    private void MovePill(bool animate)
    {
        var tabs = new[] { LookTab, RiderTab, BluetoothTab, LibraryTab, KeysTab };
        double step = LookTab.ActualWidth, x = Array.FindIndex(tabs, t => t.IsChecked == true) * step;
        TabPill.Width = Math.Max(0, step - 6);
        var shift = Motion.Shift(TabPill);
        if (!animate) { shift.X = x; return; }
        Motion.Run(shift, TranslateTransform.XProperty, shift.X, x, 380, Motion.Glide);
        TabPill.RenderTransformOrigin = new Point(0.5, 0.5);
        Motion.Keys(Motion.Scale(TabPill), ScaleTransform.ScaleXProperty, 0, [(0, 1), (110, 1.25), (400, 1)], Motion.Pop);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // == Appearance ==

    private void Look_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.BackgroundOpacity = OpacitySlider.Value / 100;
        _s.Scale = Math.Round(ScaleSlider.Value, 2);
        _s.NotchDock = NotchBox.IsChecked == true;
        _s.ShowMap = ShowMapBox.IsChecked == true;
        UpdateValueLabels();
        _main.ApplySettings();
        ScheduleSave();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => _main.ResetPosition();

    // Small notch values in a fixed order; the notch adapts its width.
    private void CompactChip_Click(object sender, RoutedEventArgs e)
    {
        _s.CompactValues = CompactChips.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        _main.ApplySettings();
        ScheduleSave();
    }

    // == Shortcuts ==

    private readonly List<HotkeyItem> _hotkeys = Hotkeys.All.Select(h => new HotkeyItem(h.Action, h.Label)).ToList();
    private HotkeyItem? _recording;

    // Shortcuts and notes: taken by another app, blocks a keyboard character, or none set.
    private void RefreshHotkeys()
    {
        foreach (var item in _hotkeys)
        {
            if (item == _recording) continue;
            var combo = Hotkeys.Get(_s, item.Action);
            item.Keys = combo?.Display ?? "None";
            string? blocked = combo is { } c && c.Modifiers.HasFlag(ModifierKeys.Control) && c.Modifiers.HasFlag(ModifierKeys.Alt)
                ? Native.AltGrCharacter(c.VirtualKey, c.Modifiers.HasFlag(ModifierKeys.Shift)) : null;
            item.SetNote(combo is null ? "" : !_main.HotkeyWorks(item.Action) ? "Used by another app. Pick another one."
                : blocked is not null ? $"Blocks “{blocked}” on your keyboard." : "", warn: true);
        }
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not HotkeyItem item || item == _recording) return;
        if (_recording is not null) StopRecording();
        _recording = item;
        _main.SuspendHotkeys();                                               // otherwise the old shortcut would trigger the action
        item.Recording = true;
        item.Keys = "Press keys …";
        item.SetNote("Esc cancels · Delete removes", warn: false);
        button.Focus();
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } item || ((FrameworkElement)sender).DataContext != item) return;
        e.Handled = true;                                                     // no key triggers the button or the window
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            item.Keys = mods == ModifierKeys.None ? "Press keys …" : KeyCombo.ModifierText(mods) + "+ …";   // shows what is already pressed
            return;
        }
        if (mods == ModifierKeys.None && key == Key.Escape) { StopRecording(); return; }
        if (mods == ModifierKeys.None && key is Key.Delete or Key.Back)
        {
            if (item.Action == HotkeyAction.ClickThrough) { item.SetNote("Click-through needs a shortcut: it's the only way back.", warn: true); return; }
            Hotkeys.Set(_s, item.Action, null);
            StopRecording();
            return;
        }
        // only with Ctrl, Alt or Win: single keys would be missing in every app
        if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            item.SetNote("Use it with Ctrl, Alt or Win.", warn: true);
            return;
        }
        var combo = new KeyCombo(mods, key);
        var taken = Hotkeys.All.FirstOrDefault(h => h.Action != item.Action && Hotkeys.Get(_s, h.Action) == combo);
        if (taken.Label is not null) { item.SetNote($"Already used for “{taken.Label}”.", warn: true); return; }
        Hotkeys.Set(_s, item.Action, combo);
        StopRecording();
    }

    // Space and Enter click a button on key up: not while recording.
    private void HotkeyButton_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_recording is not null) e.Handled = true;
    }

    private void HotkeyButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recording is not null && ((FrameworkElement)sender).DataContext == _recording) StopRecording();
    }

    // Stops recording: registers the shortcuts again and refreshes the notes.
    private void StopRecording()
    {
        if (_recording is null) return;
        _recording.Recording = false;
        _recording = null;
        _main.ApplyHotkeys();
        RefreshHotkeys();
        ScheduleSave();
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is not null) { _recording.Recording = false; _recording = null; }
        _s.Hotkeys.Clear();
        _main.ApplyHotkeys();
        RefreshHotkeys();
        ScheduleSave();
    }

    private void UpdateValueLabels()
    {
        OpacityValue.Text = $"{OpacitySlider.Value:0} %";
        ScaleValue.Text = $"{ScaleSlider.Value:0.00}×";
        DifficultyValue.Text = $"{DifficultySlider.Value:0} %";
    }

    // == Rider ==

    private void Rider_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateValueLabels();
        bool ftp = Check(FtpBox, 50, 600, out double ftpW), rider = Check(RiderBox, 30, 200, out double riderKg);
        bool bike = Check(BikeBox, 3, 40, out double bikeKg), cda = Check(CdaBox, 0.15, 0.6, out double cdaM2);
        if (ftp) _s.FtpW = (int)Math.Round(ftpW);
        if (rider) _s.RiderKg = riderKg;
        if (bike) _s.BikeKg = bikeKg;
        if (cda) _s.CdA = cdaM2;
        _s.Difficulty = DifficultySlider.Value / 100;
        RiderError.Visibility = Show(!(ftp && rider && bike && cda));
        RiderError.Text = "Red fields aren't saved. Allowed: FTP 50–600 W, rider 30–200 kg, bike 3–40 kg, CdA 0.15–0.6 m².";
        _main.ApplySettings();
        ScheduleSave();
    }

    private static bool Check(TextBox box, double min, double max, out double value)
    {
        // comma and dot as decimal separator, whatever the Windows locale
        bool ok = double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                  && value >= min && value <= max;
        box.Tag = ok ? null : "invalid";
        return ok;
    }

    // == Bluetooth ==

    public void ShowBluetooth() => BluetoothTab.IsChecked = true;

    // Once per second while the tab is open: "Bluetooth is off" notice and connected / not connected.
    private void RefreshStatus()
    {
        BluetoothOffNotice.Visibility = Show(_main.BluetoothOff);
        var items = MyDevicesList.ItemsSource as List<MyDeviceItem>;
        if (items is null || !items.Select(i => i.Device).SequenceEqual(_s.Devices)) RefreshMyDevices();
        else foreach (var item in items) item.IsConnected = _main.IsConnected(item.Device);
    }

    private void RefreshMyDevices()
    {
        var selected = (MyDevicesList.SelectedItem as MyDeviceItem)?.Device.Address;
        var items = _s.Devices.Select(d => new MyDeviceItem(d) { IsConnected = _main.IsConnected(d) }).ToList();
        MyDevicesList.ItemsSource = items;
        MyDevicesList.SelectedItem = items.FirstOrDefault(i => i.Device.Address == selected);
        MyDevicesEmpty.Visibility = Show(items.Count == 0);
    }

    private async void ConnectSaved_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MyDeviceItem item) return;
        await UseAsync(item.Device.Address, item.Device.Name, item.Device.Kind);
    }

    private void ForgetSaved_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MyDeviceItem item) return;
        _s.Devices.Remove(item.Device);
        _s.Save();
        RefreshMyDevices();
    }

    private async void ScanTrainer_Click(object sender, RoutedEventArgs e) => await ScanAsync(BleUuids.FitnessMachineService);

    private async void ScanStrap_Click(object sender, RoutedEventArgs e) => await ScanAsync(BleUuids.HeartRateService);

    private async Task ScanAsync(Guid service)
    {
        BleInfo.Text = "Searching for 8 s …";
        DeviceList.ItemsSource = null;
        DeviceEmpty.Visibility = Visibility.Collapsed;
        try
        {
            Guid? filter = AllDevicesBox.IsChecked == true ? null : service;
            var devices = await BleScan.ScanAllAsync(filter, TimeSpan.FromSeconds(8));
            DeviceList.ItemsSource = devices.OrderBy(d => d.Name == "Unnamed").ThenBy(d => d.Name)
                .Select(d => new DeviceItem(d.Address, d.Name, $"Address {d.Address:X12}")).ToList();
            DeviceEmpty.Visibility = Show(devices.Count == 0);
            BleInfo.Text = devices.Count == 0
                ? "Nothing found. Close other apps using the device."
                : "Tap a device, then save it as trainer or HR strap.";
        }
        catch (Exception ex) { BleInfo.Text = ex.Message; }
    }

    private async void UseAsTrainer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceItem device) await UseAsync(device.Address, device.Name, "Trainer");
    }

    private async void UseAsStrap_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceItem device) await UseAsync(device.Address, device.Name, "Pulsgurt");
    }

    // Saves the device (listed under "My devices") and connects it as trainer or heart rate strap.
    private async Task UseAsync(ulong address, string name, string kind)
    {
        bool trainer = kind == "Trainer";
        if (trainer) _s.TrainerAddress = address; else _s.StrapAddress = address;
        _s.Remember(address, name, kind);
        _s.Save();
        RefreshMyDevices();
        BleInfo.Text = "Connecting …";
        bool ok = trainer ? await _main.ReconnectTrainerAsync() : await _main.ReconnectStrapAsync();
        BleInfo.Text = ok ? $"{name} connected." : "Not connected yet. I'll keep trying.";
        RefreshStatus();
    }

    // == Library ==

    private void RefreshLibrary()
    {
        var items = _library.Routes.Select(f => new LibraryItem(f, Path.GetFileNameWithoutExtension(f))).ToList();
        LibraryList.ItemsSource = items;
        LibraryEmpty.Visibility = Show(items.Count == 0);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GPX routes (*.gpx)|*.gpx", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            try { _library.Import(file); }
            catch (Exception ex) { MessageBox.Show(this, $"{Path.GetFileName(file)}: {ex.Message}", "Import failed"); }
        }
        RefreshLibrary();
    }

    private void LoadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is LibraryItem item) _main.LoadFile(item.FilePath);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not LibraryItem item) return;
        if (MessageBox.Show(this, $"Delete {item.Label}?", "Library", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { _library.Delete(item.FilePath); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Delete failed"); }
        RefreshLibrary();
    }

    // == Header avatar: same reactions as in the overlay ==

    private readonly List<long> _slaps = new();

    private void Avatar_MouseEnter(object sender, MouseEventArgs e) => HeaderAvatar.Hover(true);

    private void Avatar_MouseLeave(object sender, MouseEventArgs e) => HeaderAvatar.Hover(false);

    private void Avatar_Slap(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                                                     // otherwise dragging starts
        long now = Environment.TickCount64;
        _slaps.RemoveAll(t => now - t > 1700);
        _slaps.Add(now);
        HeaderAvatar.Slap();
        if (_slaps.Count < 3) return;
        _slaps.Clear();
        HeaderAvatar.Dizzy();
    }

    // == Window ==

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    // shrinks back towards the gear, then closes
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_closingAnimated) return;
        _closingAnimated = true;
        Motion.PopOut(Popover, 0.92, 170, Close);
    }
}

// Row on the shortcuts page: action, shortcut and a note below (warnings in yellow).
public sealed class HotkeyItem(HotkeyAction action, string label) : INotifyPropertyChanged
{
    private string _keys = "", _note = "";
    private bool _recording, _warn;
    public HotkeyAction Action { get; } = action;
    public string Label { get; } = label;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Keys { get => _keys; set => Set(ref _keys, value); }
    public string Note => _note;
    public bool HasNote => _note.Length > 0;
    public bool Warn => _warn;
    public string? RecordingTag => _recording ? "recording" : null;

    public bool Recording
    {
        get => _recording;
        set { _recording = value; Changed(nameof(RecordingTag)); }
    }

    public void SetNote(string note, bool warn)
    {
        _note = note;
        _warn = warn;
        Changed(nameof(Note)); Changed(nameof(HasNote)); Changed(nameof(Warn));
    }

    private void Set(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        if (field == value) return;
        field = value;
        Changed(name);
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Entry under "My devices". IsConnected raises change notifications so the list isn't rebuilt.
public sealed class MyDeviceItem(SavedDevice device) : INotifyPropertyChanged
{
    private bool _connected;
    public SavedDevice Device { get; } = device;
    public string Name => Device.Name;
    public string Kind => Device.Kind;
    public string KindLabel => Device.Kind == "Trainer" ? "Trainer" : "HR strap";   // Kind stays the key in settings.json
    public string Icon => Device.Kind == "Trainer" ? "" : "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsConnected
    {
        get => _connected;
        set
        {
            if (_connected == value) return;
            _connected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
        }
    }
}

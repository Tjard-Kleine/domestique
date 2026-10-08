using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Domestique.Ble;
using Domestique.Core.Library;
using Domestique.Core.Protocol;

namespace Domestique.App;

public sealed record DeviceItem(ulong Address, string Name, string Detail);
public sealed record LibraryItem(string FilePath, string Label);

// Jede Änderung wirkt sofort. Ungültige Eingaben werden rot markiert und nicht übernommen.
// Gespeichert wird kurz nach der letzten Änderung, damit ein gezogener Regler nicht bei jedem Pixel schreibt.
public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private readonly LibraryStore _library;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool _loading = true;

    public SettingsWindow(MainWindow main, AppSettings settings, LibraryStore library)
    {
        InitializeComponent();
        _main = main;
        _s = settings;
        _library = library;
        if (main.IsVisible) Owner = main;                                     // bleibt über dem Overlay und schließt mit ihm
        _save.Tick += (_, _) => { _save.Stop(); _s.Save(); };
        _status.Tick += (_, _) => RefreshStatus();

        StandardTile.IsChecked = _s.Theme == "Standard";
        TourTile.IsChecked = _s.Theme == "Tour";
        VintageTile.IsChecked = _s.Theme == "Vintage";
        OpacitySlider.Value = _s.BackgroundOpacity * 100;
        ScaleSlider.Value = _s.Scale;
        NotchBox.IsChecked = _s.NotchDock;
        ShowMapBox.IsChecked = _s.ShowMap;
        FtpBox.Text = _s.FtpW.ToString();
        RiderBox.Text = _s.RiderKg.ToString();
        BikeBox.Text = _s.BikeKg.ToString();
        CdaBox.Text = _s.CdA.ToString();
        DifficultySlider.Value = _s.Difficulty * 100;
        UpdateValueLabels();
        RefreshLibrary();
        HeaderAvatar.Mood = Mood.Idle;
        _loading = false;
        Closed += (_, _) => { _status.Stop(); if (_save.IsEnabled) { _save.Stop(); _s.Save(); } };
    }

    private void ScheduleSave()
    {
        _save.Stop();
        _save.Start();
    }

    // ---------- Reiter ----------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (LookPage is null) return;                                         // während InitializeComponent
        LookPage.Visibility = Show(LookTab.IsChecked == true);
        RiderPage.Visibility = Show(RiderTab.IsChecked == true);
        BluetoothPage.Visibility = Show(BluetoothTab.IsChecked == true);
        LibraryPage.Visibility = Show(LibraryTab.IsChecked == true);
        if (BluetoothTab.IsChecked == true) { RefreshStatus(); _status.Start(); } else _status.Stop();
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // ---------- Darstellung ----------

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string theme }) return;
        _s.Theme = theme;
        _main.ApplySettings();
        ScheduleSave();
    }

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

    private void UpdateValueLabels()
    {
        OpacityValue.Text = $"{OpacitySlider.Value:0} %";
        ScaleValue.Text = $"{ScaleSlider.Value:0.00}×";
        DifficultyValue.Text = $"{DifficultySlider.Value:0} %";
    }

    // ---------- Fahrer ----------

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
        RiderError.Text = "Rot markierte Felder werden nicht übernommen. Erlaubt: FTP 50–600 W, Fahrer 30–200 kg, Rad 3–40 kg, CdA 0,15–0,6 m².";
        _main.ApplySettings();
        ScheduleSave();
    }

    private static bool Check(TextBox box, double min, double max, out double value)
    {
        bool ok = double.TryParse(box.Text, out value) && value >= min && value <= max;
        box.Tag = ok ? null : "invalid";
        return ok;
    }

    // ---------- Bluetooth ----------

    private void RefreshStatus()
    {
        TrainerStatusText.Text = _main.TrainerStatus;
        StrapStatusText.Text = _main.StrapStatus;
        TrainerDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _main.TrainerStatus.EndsWith("verbunden") ? "PositiveBrush" : "MutedBrush");
        StrapDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _main.StrapStatus == "Pulsgurt verbunden" ? "PositiveBrush" : "MutedBrush");
    }

    private async void ScanTrainer_Click(object sender, RoutedEventArgs e) => await ScanAsync(BleUuids.FitnessMachineService);

    private async void ScanStrap_Click(object sender, RoutedEventArgs e) => await ScanAsync(BleUuids.HeartRateService);

    private async Task ScanAsync(Guid service)
    {
        BleInfo.Text = "Suche 8 Sekunden …";
        DeviceList.ItemsSource = null;
        DeviceEmpty.Visibility = Visibility.Collapsed;
        try
        {
            Guid? filter = AllDevicesBox.IsChecked == true ? null : service;
            var devices = await BleScan.ScanAllAsync(filter, TimeSpan.FromSeconds(8));
            DeviceList.ItemsSource = devices.OrderBy(d => d.Name == "Unbenannt").ThenBy(d => d.Name)
                .Select(d => new DeviceItem(d.Address, d.Name, $"Adresse {d.Address:X12}")).ToList();
            DeviceEmpty.Visibility = Show(devices.Count == 0);
            BleInfo.Text = devices.Count == 0
                ? "Nichts gefunden. Hersteller-App und Zwift geschlossen? Ein gerade verbundenes Gerät funkt meist nicht."
                : "Gerät antippen und unten zuordnen.";
        }
        catch (Exception ex) { BleInfo.Text = ex.Message; }
    }

    private async void UseAsTrainer_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem device) { BleInfo.Text = "Erst ein Gerät auswählen."; return; }
        _s.TrainerAddress = device.Address;
        _s.Save();
        BleInfo.Text = "Verbinde Trainer …";
        BleInfo.Text = await _main.ReconnectTrainerAsync()
            ? "Trainer verbunden und gespeichert."
            : "Verbindung klappt noch nicht, die App versucht es weiter.";
        RefreshStatus();
    }

    private async void UseAsStrap_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem device) { BleInfo.Text = "Erst ein Gerät auswählen."; return; }
        _s.StrapAddress = device.Address;
        _s.Save();
        BleInfo.Text = "Verbinde Pulsgurt …";
        BleInfo.Text = await _main.ReconnectStrapAsync()
            ? "Pulsgurt verbunden und gespeichert."
            : "Verbindung klappt noch nicht, die App versucht es weiter.";
        RefreshStatus();
    }

    // ---------- Bibliothek ----------

    private void RefreshLibrary()
    {
        var items = _library.Routes.Select(f => new LibraryItem(f, Path.GetFileNameWithoutExtension(f))).ToList();
        LibraryList.ItemsSource = items;
        LibraryEmpty.Visibility = Show(items.Count == 0);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GPX-Strecken (*.gpx)|*.gpx", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            try { _library.Import(file); }
            catch (Exception ex) { MessageBox.Show(this, $"{Path.GetFileName(file)}: {ex.Message}", "Import fehlgeschlagen"); }
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
        if (MessageBox.Show(this, item.Label + " löschen?", "Bibliothek", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { _library.Delete(item.FilePath); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Löschen fehlgeschlagen"); }
        RefreshLibrary();
    }

    // ---------- Fenster ----------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Quit_Click(object sender, RoutedEventArgs e) => _main.Close();
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using Domestique.Ble;
using Domestique.Core.Library;
using Domestique.Core.Protocol;

namespace Domestique.App;

public sealed record DeviceItem(ulong Address, string Label);
public sealed record LibraryItem(string FilePath, string Label);

public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private readonly LibraryStore _library;

    public SettingsWindow(MainWindow main, AppSettings settings, LibraryStore library)
    {
        InitializeComponent();
        _main = main;
        _s = settings;
        _library = library;
        Owner = main;

        BackgroundBox.Text = _s.BackgroundColor;
        TextColorBox.Text = _s.TextColor;
        AccentBox.Text = _s.AccentColor;
        OpacitySlider.Value = _s.BackgroundOpacity * 100;
        ScaleSlider.Value = _s.Scale;
        ShowMapBox.IsChecked = _s.ShowMap;
        RiderBox.Text = _s.RiderKg.ToString();
        BikeBox.Text = _s.BikeKg.ToString();
        CdaBox.Text = _s.CdA.ToString();
        DifficultySlider.Value = _s.Difficulty * 100;
        RefreshLibrary();
    }

    // ---------- Darstellung und Fahrer: nichts wird übernommen, solange ein Feld ungültig ist ----------

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!IsColor(BackgroundBox.Text) || !IsColor(TextColorBox.Text) || !IsColor(AccentBox.Text))
        {
            MessageBox.Show(this, "Farben bitte als #RRGGBB angeben, z. B. #FF5A36.", "Einstellungen");
            return;
        }
        if (!InRange(RiderBox.Text, 30, 200, out double rider) || !InRange(BikeBox.Text, 3, 40, out double bike)
            || !InRange(CdaBox.Text, 0.15, 0.6, out double cda))
        {
            MessageBox.Show(this, "Bitte Zahlen eintragen: Fahrer 30–200 kg, Rad 3–40 kg, CdA 0,15–0,6 m².", "Einstellungen");
            return;
        }
        _s.BackgroundColor = BackgroundBox.Text.Trim();
        _s.TextColor = TextColorBox.Text.Trim();
        _s.AccentColor = AccentBox.Text.Trim();
        _s.BackgroundOpacity = OpacitySlider.Value / 100;
        _s.Scale = ScaleSlider.Value;
        _s.ShowMap = ShowMapBox.IsChecked == true;
        _s.RiderKg = rider;
        _s.BikeKg = bike;
        _s.CdA = cda;
        _s.Difficulty = DifficultySlider.Value / 100;
        _s.Save();
        _main.ApplySettings();                                    // wirkt sofort, ohne Neustart
    }

    private static bool IsColor(string text)
    {
        try { return ColorConverter.ConvertFromString(text.Trim()) is Color; }
        catch { return false; }
    }

    private static bool InRange(string text, double min, double max, out double value) =>
        double.TryParse(text, out value) && value >= min && value <= max;

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => _main.ResetPosition();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Bluetooth ----------

    private async void ScanTrainer_Click(object sender, RoutedEventArgs e) => await ScanAsync(BleUuids.FitnessMachineService);

    private async void ScanStrap_Click(object sender, RoutedEventArgs e) => await ScanAsync(BleUuids.HeartRateService);

    private async Task ScanAsync(Guid service)
    {
        BleInfo.Text = "Suche 8 Sekunden …";
        DeviceList.ItemsSource = null;
        try
        {
            Guid? filter = AllDevicesBox.IsChecked == true ? null : service;
            var devices = await BleScan.ScanAllAsync(filter, TimeSpan.FromSeconds(8));
            DeviceList.ItemsSource = devices.OrderBy(d => d.Name == "Unbenannt").ThenBy(d => d.Name)
                .Select(d => new DeviceItem(d.Address, $"{d.Name}   ({d.Address:X12})")).ToList();
            BleInfo.Text = devices.Count == 0
                ? "Nichts gefunden. Hersteller-App und Zwift geschlossen? Ein gerade verbundenes Gerät funkt meist nicht und taucht deshalb nicht auf."
                : "Gerät auswählen und unten zuordnen.";
        }
        catch (Exception ex) { BleInfo.Text = ex.Message; }
    }

    private async void UseAsTrainer_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem device) return;
        _s.TrainerAddress = device.Address;
        _s.Save();
        BleInfo.Text = "Verbinde Trainer …";
        BleInfo.Text = await _main.ReconnectTrainerAsync()
            ? "Trainer verbunden und gespeichert."
            : "Verbindung klappt noch nicht, die App versucht es weiter.";
    }

    private async void UseAsStrap_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem device) return;
        _s.StrapAddress = device.Address;
        _s.Save();
        BleInfo.Text = "Verbinde Pulsgurt …";
        BleInfo.Text = await _main.ReconnectStrapAsync()
            ? "Pulsgurt verbunden und gespeichert."
            : "Verbindung klappt noch nicht, die App versucht es weiter.";
    }

    // ---------- Bibliothek ----------

    private void RefreshLibrary() =>
        LibraryList.ItemsSource = _library.Routes
            .Select(f => new LibraryItem(f, Path.GetFileNameWithoutExtension(f)))
            .ToList();

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
}

using System.Windows;
using System.Windows.Threading;
using Domestique.Ble;
using Domestique.Core;
using Domestique.Core.Protocol;

namespace Domestique.App;

public partial class MainWindow : Window
{
    private readonly SessionState _state = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private BleTrainer? _trainer;

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer.Tick += (_, _) => Render(_state.Current);
        _uiTimer.Start();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Suche Trainer …";
            ulong? address = await BleScan.FindFirstAsync(BleUuids.FitnessMachineService, TimeSpan.FromSeconds(15));
            if (address is null) { StatusText.Text = "Kein FTMS-Trainer gefunden."; return; }
            StatusText.Text = "Verbinde …";
            _trainer = new BleTrainer(_state);
            await _trainer.ConnectAsync(address.Value);
            StatusText.Text = "Verbunden.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + ex.Message;
        }
    }

    private void Render(SessionSnapshot s) =>
        ValuesText.Text = $"{s.Power3sW} W   {s.CadenceRpm:0} U/min   {s.TrainerSpeedKmh:0.0} km/h   "
                        + (s.Connected ? "verbunden" : "getrennt");

    protected override async void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        if (_trainer is not null) await _trainer.DisposeAsync();
        base.OnClosed(e);
    }
}
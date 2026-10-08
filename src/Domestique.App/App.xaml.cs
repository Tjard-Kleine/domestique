using System.Windows;

namespace Domestique.App;

public partial class App : Application
{
    public static readonly string[] Themes = ["Standard", "Tour", "Vintage"];
    private static string _theme = "Standard";                     // so in App.xaml vorgeladen

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyTheme(AppSettings.Load().Theme);                     // vor dem ersten Fenster, sonst blitzt der Standardstil auf
        base.OnStartup(e);
    }

    // Tauscht die Farb- und Schrift-Ressourcen aus. Alles mit DynamicResource färbt sich sofort um.
    // Unbekannte Namen (Tippfehler in settings.json) fallen auf Standard zurück.
    public static string ApplyTheme(string? name)
    {
        name = Array.Find(Themes, t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) ?? "Standard";
        if (name == _theme) return name;
        _theme = name;
        Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Domestique.App;component/Themes/{name}.xaml", UriKind.Absolute),
        };
        return name;
    }
}

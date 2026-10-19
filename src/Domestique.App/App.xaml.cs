using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Domestique.App;

public partial class App : Application
{
    public static readonly string[] Themes = ["Standard", "Tour", "Vintage"];
    private static string _theme = "Standard";                     // so in App.xaml vorgeladen

    protected override void OnStartup(StartupEventArgs e)
    {
        // Englische Oberfläche, also auch englisches Zahlenformat (31.4 km/h), egal wie Windows eingestellt ist.
        // Aufzeichnung und Dateien sind davon unabhängig, sie schreiben und lesen kulturunabhängig.
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = english;
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = english;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(english.IetfLanguageTag)));
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

using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Domestique.App;

public partial class App : Application
{
    // English UI and number format (31.4 km/h) regardless of the Windows locale. Recordings and files are culture-invariant.
    protected override void OnStartup(StartupEventArgs e)
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = english;
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = english;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(english.IetfLanguageTag)));
        base.OnStartup(e);
    }
}

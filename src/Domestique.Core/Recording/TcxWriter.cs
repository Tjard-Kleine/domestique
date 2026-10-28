using System.Globalization;
using System.Xml.Linq;

namespace Domestique.Core.Recording;

// TCX for Strava and others. Without GPS positions Strava treats the ride as indoor.
// Element order follows the Garmin schema; some portals reject other orders.
public static class TcxWriter
{
    private static readonly XNamespace Ns = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Ext = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static void Write(string path, IReadOnlyList<Sample> samples) => Build(samples).Save(path);

    public static XDocument Build(IReadOnlyList<Sample> samples)
    {
        if (samples.Count < 2) throw new ArgumentException("Too few samples for a ride.", nameof(samples));
        Sample first = samples[0], last = samples[^1];
        bool hasCadence = samples.Any(s => s.CadenceRpm > 0);     // no cadence sensor: omit the field instead of writing 0
        double kj = 0;                                            // work in kJ, roughly kcal when cycling
        for (int i = 1; i < samples.Count; i++)
            kj += Math.Max(samples[i].PowerW, 0) * (samples[i].TimeUtc - samples[i - 1].TimeUtc).TotalSeconds / 1000;
        string start = Time(first.TimeUtc);

        return new XDocument(
            new XElement(Ns + "TrainingCenterDatabase",
                new XAttribute(XNamespace.Xmlns + "ns3", Ext),
                new XElement(Ns + "Activities",
                    new XElement(Ns + "Activity", new XAttribute("Sport", "Biking"),
                        new XElement(Ns + "Id", start),
                        new XElement(Ns + "Lap", new XAttribute("StartTime", start),
                            new XElement(Ns + "TotalTimeSeconds", Inv((last.TimeUtc - first.TimeUtc).TotalSeconds, "0")),
                            new XElement(Ns + "DistanceMeters", Inv(last.DistanceM, "0.0")),
                            new XElement(Ns + "Calories", (int)Math.Round(kj)),
                            new XElement(Ns + "Intensity", "Active"),
                            new XElement(Ns + "TriggerMethod", "Manual"),
                            new XElement(Ns + "Track", samples.Select(s => Trackpoint(s, hasCadence))))))));
    }

    private static XElement Trackpoint(Sample s, bool hasCadence) => new(Ns + "Trackpoint",
        new XElement(Ns + "Time", Time(s.TimeUtc)),
        new XElement(Ns + "DistanceMeters", Inv(s.DistanceM, "0.0")),
        s.HeartRateBpm is int hr ? new XElement(Ns + "HeartRateBpm", new XElement(Ns + "Value", hr)) : null,
        hasCadence ? new XElement(Ns + "Cadence", Math.Clamp((int)Math.Round(s.CadenceRpm), 0, 254)) : null,
        new XElement(Ns + "Extensions",
            new XElement(Ext + "TPX",
                new XElement(Ext + "Speed", Inv(s.SpeedMps, "0.00")),
                new XElement(Ext + "Watts", Math.Max(s.PowerW, 0)))));

    private static string Time(DateTime utc) => utc.ToString(TimeFormat, CultureInfo.InvariantCulture);
    private static string Inv(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}

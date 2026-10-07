using Domestique.Core.Library;
using Xunit;

namespace Domestique.Tests;

public class LibraryStoreTests
{
    private const string GoodGpx = """
        <gpx><trk><trkseg>
          <trkpt lat="51.0" lon="7.0"><ele>100</ele></trkpt>
          <trkpt lat="51.0089932" lon="7.0"><ele>150</ele></trkpt>
        </trkseg></trk></gpx>
        """;

    [Fact]
    public void Imports_valid_and_rejects_broken_routes()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string good = Path.Combine(root, "gut.gpx"), bad = Path.Combine(root, "kaputt.gpx");
        File.WriteAllText(good, GoodGpx);
        File.WriteAllText(bad, "<gpx><trk><trkseg><trkpt lat=\"51\" lon=\"7\"/></trkseg></trk></gpx>");
        var store = new LibraryStore(Path.Combine(root, "lib"));

        string imported = store.Import(good);
        Assert.Throws<FormatException>(() => store.Import(bad));
        Assert.Equal(imported, Assert.Single(store.Routes));
    }

    [Fact]
    public void Rejects_other_file_types()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string file = Path.Combine(root, "plan.zwo");
        File.WriteAllText(file, "<workout_file/>");
        Assert.Throws<FormatException>(() => new LibraryStore(root).Import(file));
    }

    [Fact]
    public void Importing_a_library_file_again_keeps_it()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        var store = new LibraryStore(root);
        string path = Path.Combine(store.RoutesDir, "schon_da.gpx");
        File.WriteAllText(path, GoodGpx);
        Assert.Equal(path, store.Import(path));
        Assert.Single(store.Routes);
    }

    [Fact]
    public void Deletes_only_inside_the_library()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string outside = Path.Combine(root, "fremd.gpx");
        File.WriteAllText(outside, GoodGpx);
        var store = new LibraryStore(Path.Combine(root, "lib"));
        string inside = store.Import(outside);

        Assert.Throws<InvalidOperationException>(() => store.Delete(outside));
        store.Delete(inside);
        Assert.Empty(store.Routes);
        Assert.True(File.Exists(outside));
    }
}

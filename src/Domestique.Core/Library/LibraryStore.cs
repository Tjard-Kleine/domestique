using Domestique.Core.Routes;

namespace Domestique.Core.Library;

// Route library in %APPDATA%\Domestique\Routes. Files are validated before they are copied in.
public sealed class LibraryStore
{
    public string RoutesDir { get; }

    public LibraryStore(string root)
    {
        RoutesDir = Path.Combine(root, "Routes");
        Directory.CreateDirectory(RoutesDir);
    }

    public IReadOnlyList<string> Routes => Directory.GetFiles(RoutesDir, "*.gpx").Order().ToList();

    public string Import(string sourcePath)
    {
        if (!Path.GetExtension(sourcePath).Equals(".gpx", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Only .gpx routes are supported.");
        GpxLoader.Load(File.ReadAllText(sourcePath), "Check");                 // throws on a broken file
        string target = Path.Combine(RoutesDir, Path.GetFileName(sourcePath));
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, target, overwrite: true);
        return target;
    }

    public void Delete(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(RoutesDir), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only library files can be deleted.");
        File.Delete(path);
    }
}

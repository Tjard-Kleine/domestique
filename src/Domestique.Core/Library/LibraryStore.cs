using Domestique.Core.Routes;

namespace Domestique.Core.Library;

// Strecken-Bibliothek unter %APPDATA%\Domestique\Routes. Jede Datei wird vor dem Kopieren geprüft,
// damit nur lesbare Strecken darin landen.
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
            throw new FormatException("Nur .gpx-Strecken werden unterstützt.");
        GpxLoader.Load(File.ReadAllText(sourcePath), "Prüfung");                 // wirft bei kaputter Datei
        string target = Path.Combine(RoutesDir, Path.GetFileName(sourcePath));
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, target, overwrite: true);
        return target;
    }

    public void Delete(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(RoutesDir), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nur Dateien aus der Bibliothek lassen sich löschen.");
        File.Delete(path);
    }
}

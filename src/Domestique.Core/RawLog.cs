using System.Text;

namespace Domestique.Core;

// Rohdaten-Log unter %LOCALAPPDATA%\Domestique\raw.log. Die Datei bleibt offen statt pro Zeile neu geöffnet zu
// werden, jede Zeile wird trotzdem sofort geschrieben. Ist sie beim Start größer als 10 MB, wird sie zu raw.old.log.
public static class RawLog
{
    private const long MaxBytes = 10 * 1024 * 1024;
    public static string LogFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Domestique", "raw.log");
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static void Write(string channel, byte[] bytes) => Note($"{channel} {Convert.ToHexString(bytes)}");

    public static void Note(string text)
    {
        lock (Gate)
        {
            try
            {
                _writer ??= Open();
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
            }
            catch (IOException) { }                               // das Log darf die App nie stören
        }
    }

    private static StreamWriter Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        var file = new FileInfo(LogFile);
        if (file.Exists && file.Length > MaxBytes) file.MoveTo(Path.ChangeExtension(LogFile, ".old.log"), overwrite: true);
        var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };   // BOM: Umlaute auch in Notepad
    }
}

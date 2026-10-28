using System.Text;

namespace Domestique.Core;

// Raw data log in %LOCALAPPDATA%\Domestique\raw.log. Stays open, flushes every line,
// rotates to raw.old.log when larger than 10 MB at startup.
public static class RawLog
{
    private const long MaxBytes = 10 * 1024 * 1024;
    private static readonly string LogFile = Path.Combine(
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
            catch (IOException) { }                               // logging must never break the app
        }
    }

    private static StreamWriter Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        var file = new FileInfo(LogFile);
        if (file.Exists && file.Length > MaxBytes) file.MoveTo(Path.ChangeExtension(LogFile, ".old.log"), overwrite: true);
        var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
    }
}

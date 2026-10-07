namespace Domestique.Core;

public static class RawLog
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Domestique", "raw.log");
    private static readonly object Gate = new();

    public static void Write(string channel, byte[] bytes) => Note($"{channel} {Convert.ToHexString(bytes)}");

    public static void Note(string text)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
    }
}
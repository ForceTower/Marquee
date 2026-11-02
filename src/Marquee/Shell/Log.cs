namespace Marquee.Shell;

/// <summary>Appends to %LOCALAPPDATA%\Marquee\marquee.log (trimmed at startup when it grows past 2 MB).</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Marquee", "marquee.log");

    static Log()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2 << 20)
        {
            File.Delete(FilePath);
        }
    }

    public static void Info(string message) => Write("info", message);

    public static void Error(string message, Exception? ex = null) => Write("error", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}

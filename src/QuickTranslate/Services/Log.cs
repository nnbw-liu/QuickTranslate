using System.IO;

namespace QuickTranslate.Services;

public static class Log
{
    private static readonly object Lock = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickTranslate", "logs");

    public static void Write(string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                var f = Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(f, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
        }
        catch { }
    }

    public static void Error(Exception ex) => Write($"ERROR: {ex}");

    public static void Info(string msg) => Write(msg);
}

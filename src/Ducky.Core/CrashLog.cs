namespace Ducky.Core;

public static class CrashLog
{
    private static readonly object Lock = new();

    public static string LogPath => Path.Combine(AppPaths.AppDataDirectory, "crash.log");

    public static void Write(Exception ex)
    {
        try
        {
            var text =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}" +
                $"---{Environment.NewLine}";
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.AppDataDirectory);
                File.AppendAllText(LogPath, text);
            }
        }
        catch
        {
            // Best-effort crash logging only.
        }
    }
}

namespace Ducky.Core;

public static class DuckTrace
{
    private static readonly object Lock = new();
    private static bool _enabled;
    private static string? _logPath;

    public static bool Enabled
    {
        get
        {
            lock (Lock)
            {
                return _enabled;
            }
        }
    }

    public static string LogPath =>
        _logPath ??= Path.Combine(AppPaths.AppDataDirectory, "ducky-trace.log");

    public static void SetEnabled(bool enabled)
    {
        lock (Lock)
        {
            _enabled = enabled;
        }

        if (enabled)
        {
            Write("trace", "Diagnostic logging enabled");
        }
    }

    public static void Write(string source, string message)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {message}";
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.AppDataDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    public static void WriteSnapshot(string source, string targetProcess, float peak, bool targetAvailable, int registeredSessions, string? extra = null)
    {
        if (!_enabled)
        {
            return;
        }

        var suffix = string.IsNullOrEmpty(extra) ? string.Empty : $" | {extra}";
        Write(source, $"target={targetProcess} peak={peak:F4} available={targetAvailable} eventHandlers={registeredSessions}{suffix}");
    }
}

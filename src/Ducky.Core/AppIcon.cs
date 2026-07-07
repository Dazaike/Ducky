using System.Drawing;

namespace Ducky.Core;

public static class AppIcon
{
    private static Icon? _cached;

    public static Icon TrayIcon
    {
        get
        {
            _cached ??= LoadIcon();
            return (Icon)_cached.Clone();
        }
    }

    private static Icon LoadIcon()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            var fromExe = Icon.ExtractAssociatedIcon(exePath);
            if (fromExe is not null)
            {
                return fromExe;
            }
        }

        var icoPath = Path.Combine(AppContext.BaseDirectory, "ducky.ico");
        if (File.Exists(icoPath))
        {
            return new Icon(icoPath);
        }

        return SystemIcons.Application;
    }
}

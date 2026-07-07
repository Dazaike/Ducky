using Ducky.Core;

namespace Ducky.Calibrate;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var initialTarget = args.FirstOrDefault(a => !a.StartsWith('-'));
        Application.Run(new CalibrateApplicationContext(initialTarget));
    }
}

internal sealed class CalibrateApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly CalibrateForm _form;

    public CalibrateApplicationContext(string? initialTarget)
    {
        _form = new CalibrateForm(initialTarget);
        _form.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _form.Hide();
            }
        };

        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon.TrayIcon,
            Text = AppBranding.CalibrateAppName,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _trayIcon.ContextMenuStrip!.Items.Add("Show Window", null, (_, _) => ShowWindow());
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayIcon.DoubleClick += (_, _) => ShowWindow();

        ShowWindow();
    }

    private void ShowWindow()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _form.CloseForExit();
        _trayIcon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _form.Dispose();
        }

        base.Dispose(disposing);
    }
}

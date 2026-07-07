using Ducky.Core;
using Ducky.Core.Audio;
using Ducky.Core.Config;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ducky;

static class Program
{
    [STAThread]
    static void Main()
    {
        ComWrappers.RegisterForMarshalling(WinFormsComInterop.WinFormsComWrappers.Instance);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                CrashLog.Write(ex);
            }
            else
            {
                CrashLog.Write(new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception"));
            }

            Environment.Exit(1);
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            Environment.Exit(1);
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new DuckApplicationContext());
    }
}

internal sealed class DuckApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private DuckAppSettings _settings;
    private readonly DuckingEngineManager _manager;
    private readonly ToolStripMenuItem _enableItem;
    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _traceItem;

    public DuckApplicationContext()
    {
        _settings = SettingsStore.LoadAppSettings();
        _manager = new DuckingEngineManager(_settings);
        _manager.IsEnabled = _settings.Enabled;
        _manager.StateChanged += UpdateTrayText;
        _manager.SetTraceEnabled(_settings.TraceEnabled);

        _enableItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled())
        {
            Checked = _settings.Enabled
        };
        _stateItem = new ToolStripMenuItem("State: Idle") { Enabled = false };
        _traceItem = new ToolStripMenuItem("Diagnostic logging", null, (_, _) => ToggleTrace())
        {
            Checked = _settings.TraceEnabled
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enableItem);
        menu.Items.Add(_stateItem);
        menu.Items.Add(_traceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open trace log...", null, (_, _) => OpenTraceLog());
        menu.Items.Add("Log snapshot now", null, (_, _) => LogSnapshot());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Import calibration...", null, (_, _) => ImportCalibration());
        menu.Items.Add("Restore audio now", null, (_, _) => RestoreAudio());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon.TrayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };

        UpdateTrayText();
        try
        {
            _manager.ForceRestoreAll();
            _manager.Start();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private void RestoreAudio()
    {
        _manager.ForceRestoreAll();
        UpdateTrayText();
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        _manager.IsEnabled = _settings.Enabled;
        _enableItem.Checked = _settings.Enabled;
        SettingsStore.SaveAppSettings(_settings);
        UpdateTrayText();
    }

    private void ToggleTrace()
    {
        _settings.TraceEnabled = !_settings.TraceEnabled;
        _traceItem.Checked = _settings.TraceEnabled;
        _manager.SetTraceEnabled(_settings.TraceEnabled);
        SettingsStore.SaveAppSettings(_settings);

        if (_settings.TraceEnabled)
        {
            MessageBox.Show(
                $"Diagnostic logging enabled.\n\nLog file:\n{DuckTrace.LogPath}",
                "Trace logging",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void OpenTraceLog()
    {
        if (!File.Exists(DuckTrace.LogPath))
        {
            MessageBox.Show(
                $"No log file yet.\n\nEnable Diagnostic logging first.\n\n{DuckTrace.LogPath}",
                "Trace log",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(DuckTrace.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log:\n{ex.Message}", "Trace log", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LogSnapshot()
    {
        if (!_settings.TraceEnabled)
        {
            _settings.TraceEnabled = true;
            _traceItem.Checked = true;
            _manager.SetTraceEnabled(true);
            SettingsStore.SaveAppSettings(_settings);
        }

        _manager.WriteDiagnosticSnapshots();
        MessageBox.Show($"Snapshot written to:\n{DuckTrace.LogPath}", "Trace log", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _settings = form.BuildSettings();
            SettingsStore.SaveAppSettings(_settings);
            _manager.UpdateSettings(_settings);
            _enableItem.Checked = _settings.Enabled;
            UpdateTrayText();
        }
    }

    private void ImportCalibration()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import calibration.json",
            Filter = "Calibration JSON|*.json|All files|*.*",
            InitialDirectory = AppPaths.CalibrationDirectory
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var calibration = SettingsStore.LoadCalibration(dialog.FileName);
        if (calibration is null)
        {
            MessageBox.Show("Could not read calibration file.", "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var profile = _settings.Profiles.FirstOrDefault(p =>
                          p.TargetProcess.Equals(calibration.TargetProcess, StringComparison.OrdinalIgnoreCase) ||
                          p.Id.Equals(calibration.TargetProcess, StringComparison.OrdinalIgnoreCase))
                      ?? _settings.Profiles.FirstOrDefault();

        if (profile is null)
        {
            MessageBox.Show("No profile found to apply calibration to.", "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _settings.ApplyCalibration(profile, calibration);
        SettingsStore.SaveCalibrationForProfile(calibration, profile.Id);
        SettingsStore.SaveAppSettings(_settings);
        _manager.UpdateSettings(_settings);

        MessageBox.Show(
            $"Imported calibration for {profile.DisplayName}.\nDurationThresholdMs set to {calibration.SuggestedThresholdMs}.",
            "Calibration imported",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        var enabledText = _settings.Enabled ? "On" : "Off";
        var activeStates = _manager.GetStates().Where(s => s.State != DuckingState.Idle).ToList();
        var stateText = activeStates.Count > 0
            ? string.Join(", ", activeStates.Select(s => $"{s.Profile.DisplayName}: {s.State}"))
            : "Idle";
        _stateItem.Text = $"State: {stateText}";
        _trayIcon.Text = $"{AppBranding.AppName} ({enabledText}) — {stateText}";
    }

    private void ExitApp()
    {
        _manager.ForceRestoreAll();
        _manager.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _manager.Dispose();
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}

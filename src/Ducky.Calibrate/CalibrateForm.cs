using Ducky.Core;
using Ducky.Core.Audio;
using Ducky.Core.Config;

namespace Ducky.Calibrate;

public sealed class CalibrateForm : Form
{
    private const int MaxEvents = 200;

    private readonly ComboBox _targetSelector;
    private readonly TextBox _profileIdInput;
    private readonly PeakMonitor _monitor;
    private readonly List<PlaybackEvent> _events = new();
    private readonly ListBox _eventList;
    private readonly Label _statusLabel;
    private readonly Label _statsLabel;
    private readonly Label _suggestedLabel;
    private readonly NumericUpDown _headroomInput;
    private readonly Button _exportButton;
    private readonly Button _clearButton;
    private bool _exitRequested;
    private string _targetProcess = "beeper";
    private string _profileId = "beeper";

    public CalibrateForm(string? initialTarget = null)
    {
        Text = $"{AppBranding.AppName} — Calibrate";
        Icon = AppIcon.TrayIcon;
        Width = 720;
        Height = 600;
        StartPosition = FormStartPosition.CenterScreen;

        var targetPanel = new Panel { Dock = DockStyle.Top, Height = 72 };
        targetPanel.Controls.Add(new Label
        {
            Text = "Target app:",
            AutoSize = true,
            Location = new Point(8, 10)
        });
        _targetSelector = new ComboBox
        {
            Location = new Point(90, 8),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _targetSelector.Items.AddRange(["beeper", "Discord"]);
        _targetSelector.SelectedIndexChanged += (_, _) => ChangeTarget();
        targetPanel.Controls.Add(_targetSelector);
        targetPanel.Controls.Add(new Label
        {
            Text = "Profile id:",
            AutoSize = true,
            Location = new Point(8, 40)
        });
        _profileIdInput = new TextBox
        {
            Location = new Point(90, 38),
            Width = 200,
            Text = "beeper"
        };
        targetPanel.Controls.Add(_profileIdInput);

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(8, 6, 8, 0),
            Text = "Select a target app..."
        };

        _statsLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(8, 4, 8, 0),
            Text = "No events recorded yet."
        };

        _suggestedLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(8, 4, 8, 0),
            Text = "Suggested threshold: —"
        };

        var headroomPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
        headroomPanel.Controls.Add(new Label
        {
            Text = "Headroom (ms):",
            AutoSize = true,
            Location = new Point(8, 10)
        });
        _headroomInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 5000,
            Value = 150,
            Location = new Point(120, 8),
            Width = 80
        };
        _headroomInput.ValueChanged += (_, _) => UpdateStats();
        headroomPanel.Controls.Add(_headroomInput);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        _exportButton = new Button { Text = "Export calibration", AutoSize = true };
        _exportButton.Click += (_, _) => ExportCalibration();
        _clearButton = new Button { Text = "Clear", AutoSize = true };
        _clearButton.Click += (_, _) => ClearEvents();
        buttonPanel.Controls.Add(_exportButton);
        buttonPanel.Controls.Add(_clearButton);

        _eventList = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            IntegralHeight = false
        };

        Controls.Add(_eventList);
        Controls.Add(buttonPanel);
        Controls.Add(headroomPanel);
        Controls.Add(_suggestedLabel);
        Controls.Add(_statsLabel);
        Controls.Add(_statusLabel);
        Controls.Add(targetPanel);

        _monitor = new PeakMonitor(_targetProcess);
        _monitor.PlaybackCompleted += OnPlaybackCompleted;

        if (!string.IsNullOrWhiteSpace(initialTarget))
        {
            var index = _targetSelector.Items.IndexOf(initialTarget);
            _targetSelector.SelectedIndex = index >= 0 ? index : 0;
        }
        else
        {
            _targetSelector.SelectedIndex = 0;
        }

        _monitor.Start();

        var statusTimer = new System.Windows.Forms.Timer { Interval = 200 };
        statusTimer.Tick += (_, _) => UpdateStatus();
        statusTimer.Start();
        UpdateStatus();
    }

    private void ChangeTarget()
    {
        _targetProcess = _targetSelector.SelectedItem?.ToString() ?? "beeper";
        _profileId = _profileIdInput.Text = _targetProcess;
        _monitor.TargetProcess = _targetProcess;
        ClearEvents();
        UpdateStatus();
    }

    public void CloseForExit()
    {
        _exitRequested = true;
        _monitor.Dispose();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _monitor.Dispose();
        base.OnFormClosing(e);
    }

    private void OnPlaybackCompleted(PlaybackEvent playbackEvent)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnPlaybackCompleted(playbackEvent));
            return;
        }

        _events.Add(playbackEvent);
        if (_events.Count > MaxEvents)
        {
            _events.RemoveAt(0);
        }

        _eventList.Items.Insert(0, playbackEvent.ToString());
        if (_eventList.Items.Count > MaxEvents)
        {
            _eventList.Items.RemoveAt(_eventList.Items.Count - 1);
        }

        UpdateStats();
    }

    private void UpdateStatus()
    {
        if (!SessionEnumerator.IsTargetAvailable(_targetProcess))
        {
            _statusLabel.Text = $"Waiting for {_targetProcess}.exe — start the app and play sounds to measure events.";
            return;
        }

        var sessionPids = SessionEnumerator.FindSessionProcessIds(_targetProcess);
        var peak = SessionEnumerator.GetPeakForTargetProcess(_targetProcess);
        var pidText = sessionPids.Count > 0
            ? $"audio session PID(s): {string.Join(", ", sessionPids)}"
            : $"running (no audio session yet, {SessionEnumerator.FindAllProcessIds(_targetProcess).Count} process(es))";

        _statusLabel.Text =
            $"Monitoring {_targetProcess}.exe ({pidText}) — live peak: {peak:F4} — trigger notifications and voice messages.";
    }

    private void UpdateStats()
    {
        if (_events.Count == 0)
        {
            _statsLabel.Text = "No events recorded yet.";
            _suggestedLabel.Text = "Suggested threshold: —";
            _exportButton.Enabled = false;
            return;
        }

        var durations = _events.Select(e => e.DurationMs).ToList();
        var summary = CalibrationStats.Compute(durations, (int)_headroomInput.Value);

        _statsLabel.Text =
            $"Events: {summary.Count}   Min: {summary.MinMs} ms   Max: {summary.MaxMs} ms   " +
            $"Avg: {summary.AverageMs:F0} ms   P95: {summary.P95Ms:F0} ms";
        _suggestedLabel.Text =
            $"Suggested threshold: {summary.SuggestedThresholdMs} ms (max notification + {(int)_headroomInput.Value} ms headroom)";
        _exportButton.Enabled = true;
    }

    private void ClearEvents()
    {
        _events.Clear();
        _eventList.Items.Clear();
        UpdateStats();
    }

    private void ExportCalibration()
    {
        if (_events.Count == 0)
        {
            return;
        }

        _profileId = _profileIdInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(_profileId))
        {
            _profileId = _targetProcess;
        }

        var durations = _events.Select(e => e.DurationMs).ToList();
        var headroom = (int)_headroomInput.Value;
        var summary = CalibrationStats.Compute(durations, headroom);

        var data = new CalibrationData
        {
            TargetProcess = _targetProcess,
            PeakThreshold = 0.01f,
            HeadroomMs = headroom,
            SuggestedThresholdMs = summary.SuggestedThresholdMs,
            MaxDurationMs = summary.MaxMs,
            MinDurationMs = summary.MinMs,
            AverageDurationMs = summary.AverageMs,
            P95DurationMs = summary.P95Ms,
            EventCount = summary.Count,
            Events = _events.Select(e => new CalibrationEventRecord
            {
                StartUtc = e.Start.ToUniversalTime(),
                EndUtc = e.End.ToUniversalTime(),
                DurationMs = e.DurationMs,
                PeakMax = e.PeakMax
            }).ToList()
        };

        var defaultPath = AppPaths.CalibrationPathForProfile(_profileId);
        using var dialog = new SaveFileDialog
        {
            Title = "Export calibration",
            Filter = "JSON files|*.json|All files|*.*",
            FileName = Path.GetFileName(defaultPath),
            InitialDirectory = AppPaths.CalibrationDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SettingsStore.SaveCalibration(data, dialog.FileName);
        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase))
        {
            TryUpdateDuckSettings(data);
        }

        MessageBox.Show(
            this,
            $"Exported {data.EventCount} events.\nSuggested DurationThresholdMs: {data.SuggestedThresholdMs}\n\nSaved to:\n{dialog.FileName}",
            "Calibration exported",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void TryUpdateDuckSettings(CalibrationData data)
    {
        try
        {
            var settings = SettingsStore.LoadAppSettings();
            var profile = settings.Profiles.FirstOrDefault(p => p.Id.Equals(data.TargetProcess, StringComparison.OrdinalIgnoreCase))
                          ?? settings.Profiles.FirstOrDefault(p =>
                              p.TargetProcess.Equals(data.TargetProcess, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return;
            }

            settings.ApplyCalibration(profile, data);
            SettingsStore.SaveAppSettings(settings);
        }
        catch
        {
            // Calibrate can run standalone without Duck settings present.
        }
    }
}

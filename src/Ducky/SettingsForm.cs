using Ducky.Core;
using Ducky.Core.Audio;
using Ducky.Core.Config;

namespace Ducky;

public sealed class SettingsForm : Form
{
    private readonly DuckAppSettings _working;
    private readonly ListBox _profileList;
    private readonly TextBox _displayName;
    private readonly TextBox _targetProcess;
    private readonly NumericUpDown _durationThreshold;
    private readonly NumericUpDown _peakThreshold;
    private readonly CheckBox _profileEnabled;
    private readonly NumericUpDown _hangTime;
    private readonly ComboBox _duckingMode;
    private readonly NumericUpDown _duckRatio;
    private readonly TrackBar _duckFade;
    private readonly Label _duckFadeLabel;
    private readonly TextBox _excludedProcesses;
    private readonly CheckBox _requireBackgroundAudio;
    private readonly TextBox _backgroundDevicePattern;
    private readonly CheckBox _onlyDuckActiveSessions;

    public SettingsForm(DuckAppSettings settings)
    {
        _working = CloneSettings(settings);
        Text = $"{AppBranding.AppName} — Settings";
        Icon = AppIcon.TrayIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 560);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 13,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _profileList = new ListBox { Dock = DockStyle.Fill, Height = 80 };
        _profileList.SelectedIndexChanged += (_, _) => LoadSelectedProfile();
        _displayName = new TextBox { Dock = DockStyle.Fill };
        _targetProcess = new TextBox { Dock = DockStyle.Fill };
        _durationThreshold = CreateNumeric(452, 0, 60000);
        _peakThreshold = CreateNumeric(0.01m, 0, 1, increment: 0.001m, decimals: 3);
        _profileEnabled = new CheckBox { Text = "Profile enabled", AutoSize = true, Checked = true };
        _hangTime = CreateNumeric(_working.HangTimeMs, 0, 10000);
        _duckingMode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _duckingMode.Items.AddRange(["Mute", "Duck", "Pause"]);
        _duckingMode.SelectedItem = _working.DuckingMode;
        _duckingMode.SelectedIndexChanged += (_, _) => UpdateFadeControls();
        _duckRatio = CreateNumeric((decimal)_working.DuckRatio, 0, 1, increment: 0.05m, decimals: 2);
        _duckFade = new TrackBar
        {
            Minimum = 50,
            Maximum = 1500,
            TickFrequency = 50,
            SmallChange = 25,
            LargeChange = 100,
            Value = Math.Clamp(_working.DuckFadeMs, 50, 1500),
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 42
        };
        _duckFadeLabel = new Label
        {
            Text = $"{Math.Clamp(_working.DuckFadeMs, 50, 1500)} ms",
            AutoSize = true,
            Padding = new Padding(8, 10, 0, 0)
        };
        _duckFade.ValueChanged += (_, _) => _duckFadeLabel.Text = $"{_duckFade.Value} ms";
        var fadePanel = new Panel { Dock = DockStyle.Fill, Height = 42 };
        fadePanel.Controls.Add(_duckFade);
        fadePanel.Controls.Add(_duckFadeLabel);
        _duckFadeLabel.Dock = DockStyle.Right;
        _duckFade.Dock = DockStyle.Fill;
        _excludedProcesses = new TextBox
        {
            Text = string.Join(", ", _working.ExcludedProcesses),
            Dock = DockStyle.Fill
        };
        _requireBackgroundAudio = new CheckBox
        {
            Text = "Only when background audio is playing",
            Checked = _working.RequireBackgroundAudio,
            AutoSize = true
        };
        _backgroundDevicePattern = new TextBox
        {
            Text = _working.BackgroundAudioDevicePattern,
            Dock = DockStyle.Fill
        };
        _onlyDuckActiveSessions = new CheckBox
        {
            Text = "Only affect apps actively playing audio",
            Checked = _working.OnlyDuckActiveSessions,
            AutoSize = true
        };

        foreach (var profile in _working.Profiles)
        {
            _profileList.Items.Add(profile);
        }

        if (_profileList.Items.Count > 0)
        {
            _profileList.SelectedIndex = 0;
        }

        AddRow(layout, 0, "Messaging apps", _profileList);
        AddRow(layout, 1, "Display name", _displayName);
        AddRow(layout, 2, "Process name", _targetProcess);
        AddRow(layout, 3, "Duration threshold (ms)", _durationThreshold);
        AddRow(layout, 4, "Peak threshold", _peakThreshold);
        AddRow(layout, 5, "", _profileEnabled);
        AddRow(layout, 6, "Hang time (ms)", _hangTime);
        AddRow(layout, 7, "Background action", _duckingMode);
        AddRow(layout, 8, "Duck ratio", _duckRatio);
        AddRow(layout, 9, "Duck fade speed", fadePanel);
        AddRow(layout, 10, "Excluded processes", _excludedProcesses);
        AddRow(layout, 11, "Background device match", _backgroundDevicePattern);
        AddRow(layout, 12, "", _requireBackgroundAudio);

        UpdateFadeControls();

        var extraPanel = new Panel { Dock = DockStyle.Bottom, Height = 28 };
        extraPanel.Controls.Add(_onlyDuckActiveSessions);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8)
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttonPanel.Controls.Add(ok);
        buttonPanel.Controls.Add(cancel);

        Controls.Add(layout);
        Controls.Add(extraPanel);
        Controls.Add(buttonPanel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public DuckAppSettings BuildSettings()
    {
        SaveSelectedProfile();
        _working.HangTimeMs = (int)_hangTime.Value;
        _working.DuckingMode = _duckingMode.SelectedItem?.ToString() ?? "Mute";
        _working.DuckRatio = (float)_duckRatio.Value;
        _working.DuckFadeMs = _duckFade.Value;
        _working.ExcludedProcesses = _excludedProcesses.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
            .ToList();
        _working.RequireBackgroundAudio = _requireBackgroundAudio.Checked;
        _working.BackgroundAudioDevicePattern = _backgroundDevicePattern.Text.Trim();
        _working.OnlyDuckActiveSessions = _onlyDuckActiveSessions.Checked;
        return CloneSettings(_working);
    }

    private void LoadSelectedProfile()
    {
        if (_profileList.SelectedItem is not TargetProfile profile)
        {
            return;
        }

        _displayName.Text = profile.DisplayName;
        _targetProcess.Text = profile.TargetProcess;
        _durationThreshold.Value = Math.Clamp(profile.DurationThresholdMs, (int)_durationThreshold.Minimum, (int)_durationThreshold.Maximum);
        _peakThreshold.Value = (decimal)profile.PeakThreshold;
        _profileEnabled.Checked = profile.Enabled;
    }

    private void SaveSelectedProfile()
    {
        if (_profileList.SelectedItem is not TargetProfile profile)
        {
            return;
        }

        profile.DisplayName = _displayName.Text.Trim();
        profile.TargetProcess = SessionEnumerator.NormalizeProcessName(_targetProcess.Text.Trim());
        profile.DurationThresholdMs = (int)_durationThreshold.Value;
        profile.PeakThreshold = (float)_peakThreshold.Value;
        profile.Enabled = _profileEnabled.Checked;
        _profileList.Refresh();
    }

    private static DuckAppSettings CloneSettings(DuckAppSettings settings) =>
        new()
        {
            Enabled = settings.Enabled,
            HangTimeMs = settings.HangTimeMs,
            DuckingMode = settings.DuckingMode,
            DuckRatio = settings.DuckRatio,
            DuckFadeMs = settings.DuckFadeMs,
            ExcludedProcesses = settings.ExcludedProcesses.ToList(),
            RequireBackgroundAudio = settings.RequireBackgroundAudio,
            BackgroundAudioDevicePattern = settings.BackgroundAudioDevicePattern,
            OnlyDuckActiveSessions = settings.OnlyDuckActiveSessions,
            IdlePollIntervalMs = settings.IdlePollIntervalMs,
            ActivePollIntervalMs = settings.ActivePollIntervalMs,
            TraceEnabled = settings.TraceEnabled,
            Profiles = settings.Profiles.Select(p => new TargetProfile
            {
                Id = p.Id,
                DisplayName = p.DisplayName,
                TargetProcess = p.TargetProcess,
                DurationThresholdMs = p.DurationThresholdMs,
                PeakThreshold = p.PeakThreshold,
                Enabled = p.Enabled
            }).ToList()
        };

    private void UpdateFadeControls()
    {
        var duckMode = string.Equals(_duckingMode.SelectedItem?.ToString(), "Duck", StringComparison.OrdinalIgnoreCase);
        _duckFade.Enabled = duckMode;
        _duckFadeLabel.Enabled = duckMode;
        _duckRatio.Enabled = duckMode;
    }

    private static NumericUpDown CreateNumeric(decimal value, decimal min, decimal max, decimal increment = 1, int decimals = 0) =>
        new()
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Increment = increment,
            DecimalPlaces = decimals,
            Dock = DockStyle.Left,
            Width = 120
        };

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 6, 0, 0)
        }, 0, row);
        layout.Controls.Add(control, 1, row);
    }
}

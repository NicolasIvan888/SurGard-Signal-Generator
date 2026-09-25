using System.Diagnostics;

namespace SurGardSignalGenerator;

public sealed class MainForm : Form
{
    private readonly TextBox _host = new() { Text = "127.0.0.1", Width = 150 };
    private readonly NumericUpDown _rate = new()
    {
        Minimum = 1,
        Maximum = 200,
        DecimalPlaces = 1,
        Increment = 1,
        Value = 10,
        Width = 90
    };
    private readonly NumericUpDown _duration = new()
    {
        Minimum = 1,
        Maximum = 86400,
        Value = 60,
        ThousandsSeparator = true,
        Width = 100
    };
    private readonly NumericUpDown _timeout = new()
    {
        Minimum = 250,
        Maximum = 30000,
        Increment = 250,
        Value = 4000,
        Width = 100
    };
    private readonly CheckBox _port1 = new() { Text = "11001 (1xxxx)", Checked = true, AutoSize = true };
    private readonly CheckBox _port2 = new() { Text = "11002 (2xxxx)", Checked = true, AutoSize = true };
    private readonly CheckBox _port3 = new() { Text = "11003 (3xxxx)", Checked = true, AutoSize = true };
    private readonly ComboBox _order = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox _confirmation = new()
    {
        Text = "Confirm că serverul țintă este un sistem de test pe care sunt autorizat să îl folosesc.",
        AutoSize = true
    };
    private readonly Button _start = new() { Text = "START", Width = 130, Height = 42, Enabled = false };
    private readonly Button _stop = new() { Text = "STOP", Width = 130, Height = 42, Enabled = false };
    private readonly Button _openReports = new() { Text = "Deschide rapoarte", AutoSize = true };
    private readonly Label _state = MakeValueLabel("OPRIT", Color.DarkRed);
    private readonly Label _elapsed = MakeValueLabel("00:00:00");
    private readonly Label _scheduled = MakeValueLabel("0");
    private readonly Label _ack = MakeValueLabel("0", Color.DarkGreen);
    private readonly Label _failed = MakeValueLabel("0", Color.DarkRed);
    private readonly Label _inFlight = MakeValueLabel("0");
    private readonly Label _latency = MakeValueLabel("0 / 0 ms");
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White
    };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 200 };
    private readonly GeneratorEngine _engine = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private readonly string _reportsDirectory;

    public MainForm()
    {
        Text = "SurGard Signal Generator";
        MinimumSize = new Size(980, 680);
        Size = new Size(1120, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        _reportsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SurGardSignalGenerator",
            "reports");

        _order.Items.AddRange(["Circular (recomandat)", "Aleator"]);
        _order.SelectedIndex = 0;

        _grid.Columns.Add("Time", "Ora");
        _grid.Columns.Add("Object", "Obiect");
        _grid.Columns.Add("Port", "Port");
        _grid.Columns.Add("Status", "Răspuns");
        _grid.Columns.Add("Latency", "Timp ms");
        _grid.Columns.Add("Detail", "Detalii");
        _grid.Columns[0].FillWeight = 85;
        _grid.Columns[1].FillWeight = 60;
        _grid.Columns[2].FillWeight = 55;
        _grid.Columns[3].FillWeight = 55;
        _grid.Columns[4].FillWeight = 65;
        _grid.Columns[5].FillWeight = 180;

        Controls.Add(BuildLayout());

        _confirmation.CheckedChanged += (_, _) => UpdateStartAvailability();
        _port1.CheckedChanged += (_, _) => UpdateStartAvailability();
        _port2.CheckedChanged += (_, _) => UpdateStartAvailability();
        _port3.CheckedChanged += (_, _) => UpdateStartAvailability();
        _start.Click += StartClicked;
        _stop.Click += (_, _) => StopRun();
        _openReports.Click += (_, _) => OpenReports();
        _uiTimer.Tick += (_, _) => RefreshStatistics();
        _uiTimer.Start();
        FormClosing += OnFormClosing;
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            RowCount = 5,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "Generator controlat de semnale Sur-Gard compatibile",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 17F),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(8),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddSetting(settings, 0, "Server receptor", _host, "Semnale/secundă", _rate);
        AddSetting(settings, 1, "Durată (secunde)", _duration, "Timeout ACK (ms)", _timeout);
        AddSetting(settings, 2, "Ordinea obiectelor", _order, "Rute active", BuildPortsPanel());
        root.Controls.Add(settings, 0, 1);

        var warning = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.FromArgb(255, 244, 214) };
        _confirmation.Dock = DockStyle.Fill;
        warning.Controls.Add(_confirmation);
        root.Controls.Add(warning, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0)
        };
        actions.Controls.AddRange([_start, _stop, _openReports]);
        root.Controls.Add(actions, 0, 3);

        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        lower.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lower.Controls.Add(BuildStatistics(), 0, 0);
        lower.Controls.Add(_grid, 0, 1);
        root.Controls.Add(lower, 0, 4);
        return root;
    }

    private Control BuildPortsPanel()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        panel.Controls.AddRange([_port1, _port2, _port3]);
        return panel;
    }

    private Control BuildStatistics()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 2,
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        var labels = new[] { "Stare", "Timp", "Programate", "ACK 06", "Erori", "În zbor", "ACK mediu/max" };
        var values = new[] { _state, _elapsed, _scheduled, _ack, _failed, _inFlight, _latency };
        for (var index = 0; index < labels.Length; index++)
        {
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / labels.Length));
            panel.Controls.Add(new Label
            {
                Text = labels[index],
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomCenter,
                ForeColor = Color.DimGray
            }, index, 0);
            panel.Controls.Add(values[index], index, 1);
        }
        return panel;
    }

    private static void AddSetting(
        TableLayoutPanel panel,
        int row,
        string leftLabel,
        Control leftControl,
        string rightLabel,
        Control rightControl)
    {
        panel.Controls.Add(MakeSettingLabel(leftLabel), 0, row);
        panel.Controls.Add(leftControl, 1, row);
        panel.Controls.Add(MakeSettingLabel(rightLabel), 2, row);
        panel.Controls.Add(rightControl, 3, row);
        leftControl.Anchor = AnchorStyles.Left;
        rightControl.Anchor = AnchorStyles.Left;
    }

    private static Label MakeSettingLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI Semibold", 9F)
    };

    private static Label MakeValueLabel(string text, Color? color = null) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.TopCenter,
        Font = new Font("Segoe UI Semibold", 11F),
        ForeColor = color ?? Color.FromArgb(34, 60, 90)
    };

    private async void StartClicked(object? sender, EventArgs e)
    {
        var objects = PimaProtocol.DemoObjects.Where(ObjectRouteSelected).ToArray();
        if (objects.Length == 0)
        {
            MessageBox.Show("Selectează cel puțin o rută.", "Generator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_host.Text))
        {
            MessageBox.Show("Introdu adresa serverului receptor.", "Generator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var expected = (long)Math.Ceiling((double)(_rate.Value * _duration.Value));
        var confirmation = MessageBox.Show(
            $"Vor fi trimise aproximativ {expected:N0} semnale direct la {_host.Text.Trim()}\n" +
            $"Rată: {_rate.Value} semnale/secundă\nDurată: {_duration.Value} secunde\n" +
            $"Identificatori demonstrativi selectați: {objects.Length}\n" +
            "Fiecare cadru va avea un eveniment CID unic.\n\nPornești testul?",
            "Confirmare test Sur-Gard",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        SetRunning(true);
        _grid.Rows.Clear();
        _runCancellation = new CancellationTokenSource();
        var reportPath = Path.Combine(
            _reportsDirectory,
            $"surgard-test-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv");
        var options = new GeneratorOptions(
            _host.Text.Trim(),
            _rate.Value,
            TimeSpan.FromSeconds((double)_duration.Value),
            (int)_timeout.Value,
            MaximumConcurrentSignals: 256,
            RandomOrder: _order.SelectedIndex == 1,
            Objects: objects,
            ReportPath: reportPath);

        try
        {
            _runTask = _engine.RunAsync(options, _runCancellation.Token);
            await _runTask;
        }
        catch (OperationCanceledException)
        {
            // Stop requested by the operator.
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Eroare generator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetRunning(false);
            RefreshStatistics();
            _runCancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
        }
    }

    private bool ObjectRouteSelected(string objectNumber) => objectNumber[0] switch
    {
        '1' => _port1.Checked,
        '2' => _port2.Checked,
        '3' => _port3.Checked,
        _ => false
    };

    private void StopRun()
    {
        _stop.Enabled = false;
        _state.Text = "OPRIRE...";
        _state.ForeColor = Color.DarkOrange;
        _runCancellation?.Cancel();
    }

    private void SetRunning(bool running)
    {
        _start.Enabled = !running && CanStart();
        _stop.Enabled = running;
        _host.Enabled = !running;
        _rate.Enabled = !running;
        _duration.Enabled = !running;
        _timeout.Enabled = !running;
        _order.Enabled = !running;
        _port1.Enabled = !running;
        _port2.Enabled = !running;
        _port3.Enabled = !running;
        _confirmation.Enabled = !running;
        _state.Text = running ? "RULEAZĂ" : "OPRIT";
        _state.ForeColor = running ? Color.DarkGreen : Color.DarkRed;
    }

    private void UpdateStartAvailability() => _start.Enabled = _runTask is null && CanStart();

    private bool CanStart() =>
        _confirmation.Checked && (_port1.Checked || _port2.Checked || _port3.Checked);

    private void RefreshStatistics()
    {
        var snapshot = _engine.Snapshot();
        _elapsed.Text = TimeSpan.FromSeconds(snapshot.ElapsedSeconds).ToString(@"hh\:mm\:ss");
        _scheduled.Text = snapshot.Scheduled.ToString("N0");
        _ack.Text = snapshot.Acknowledged.ToString("N0");
        _failed.Text = snapshot.Failed.ToString("N0");
        _inFlight.Text = snapshot.InFlight.ToString("N0");
        _latency.Text = $"{snapshot.AverageAckMilliseconds:F1} / {snapshot.MaximumAckMilliseconds:F1} ms";

        while (_engine.RecentResults.TryDequeue(out var result))
        {
            _grid.Rows.Insert(0,
                result.Timestamp.ToString("HH:mm:ss.fff"),
                result.ObjectNumber,
                result.Port,
                result.Status,
                result.ElapsedMilliseconds.ToString("F1"),
                result.Detail);
            if (_grid.Rows.Count > 300)
            {
                _grid.Rows.RemoveAt(_grid.Rows.Count - 1);
            }
        }
    }

    private void OpenReports()
    {
        Directory.CreateDirectory(_reportsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _reportsDirectory) { UseShellExecute = true });
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_runTask is null)
        {
            return;
        }

        e.Cancel = true;
        StopRun();
        try
        {
            await _runTask;
        }
        catch
        {
            // The run result is already shown by StartClicked.
        }
        finally
        {
            FormClosing -= OnFormClosing;
            Close();
        }
    }
}

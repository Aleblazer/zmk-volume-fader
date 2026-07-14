using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>On-demand diagnostics. Nothing here polls while the dialog is idle.</summary>
sealed class DiagnosticsDialog : Form
{
    readonly MainForm.Theme _theme;
    readonly Func<string> _buildReport;
    readonly TextBox _report = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
    };

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public DiagnosticsDialog(MainForm.Theme theme, Func<string> buildReport)
    {
        _theme = theme;
        _buildReport = buildReport;
        Text = "Diagnostics";
        Font = UiFonts.Get(9.75f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(500, 360);
        ClientSize = new Size(650, 500);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = theme.Window;

        _report.BackColor = theme.CtlBg;
        _report.ForeColor = theme.Text;

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0),
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        var refresh = MakeButton("Refresh"); refresh.Click += (_, _) => RefreshReport();
        var copy = MakeButton("Copy"); copy.Click += (_, _) => CopyReport();
        var export = MakeButton("Export report…"); export.Click += (_, _) => ExportReport();
        left.Controls.Add(refresh); left.Controls.Add(copy); left.Controls.Add(export);
        buttons.Controls.Add(left, 0, 0);
        var close = MakeButton("Close", accent: true); close.Click += (_, _) => Close();
        buttons.Controls.Add(close, 1, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(14), BackColor = Color.Transparent,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_report, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);
        CancelButton = close;
        Load += (_, _) => { ApplyDark(); RefreshReport(); };
    }

    void RefreshReport()
    {
        try { _report.Text = _buildReport(); }
        catch (Exception ex) { _report.Text = $"Could not build diagnostics:\r\n\r\n{ex}"; }
        _report.SelectionStart = 0;
        _report.SelectionLength = 0;
    }

    void ExportReport()
    {
        RefreshReport();
        using var dialog = new SaveFileDialog
        {
            Title = "Export diagnostic report",
            Filter = "Text report (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"ZmkVolumeFader-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = "txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, _report.Text); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The report could not be saved.\n\n{ex.Message}",
                "Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void CopyReport()
    {
        if (_report.TextLength == 0) return;
        try { Clipboard.SetText(_report.Text); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The report could not be copied.\n\n{ex.Message}",
                "Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    RoundedButton MakeButton(string text, bool accent = false)
    {
        var b = new RoundedButton
        {
            Text = text, AutoSize = true, Padding = new Padding(11, 5, 11, 5),
            Margin = new Padding(0, 0, 6, 0), Radius = 8,
            BackColor = accent ? _theme.Accent : _theme.CtlBg,
            ForeColor = accent ? AccentText() : _theme.Text,
            Surround = _theme.Window,
        };
        b.FlatAppearance.BorderColor = accent ? _theme.Accent : _theme.CtlBorder;
        return b;
    }

    Color AccentText()
    {
        var a = _theme.Accent;
        double lum = (0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255.0;
        return lum > 0.55 ? Color.FromArgb(0x10, 0x18, 0x12) : Color.White;
    }

    void ApplyDark()
    {
        int value = _theme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int));
    }
}

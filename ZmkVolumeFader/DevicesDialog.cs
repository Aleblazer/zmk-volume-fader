using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Choose which detected fader devices the app monitors. Every HID device on our
/// vendor fader page shows up here — including gadgets that merely share the page
/// (a Steam Controller puck, say). Un-tick one to stop the app from hooking it.
/// The owner reads each row's <see cref="Row.Monitored"/> flag on OK.
/// </summary>
sealed class DevicesDialog : Form
{
    public sealed class Row
    {
        public string Key = "";
        public string Name = "";
        public string Detail = "";
        public bool OurVid;       // matches our VID — almost certainly a real fader unit
        public bool Connected;    // in use right now
        public bool Monitored;    // in/out: ticked = monitor, unticked = ignore
    }

    readonly MainForm.Theme _t;
    readonly List<Row> _rows;
    readonly ListBox _list = new() { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public DevicesDialog(MainForm.Theme t, List<Row> rows)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _t = t;
        _rows = rows;

        Text = "Fader Devices";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 460);
        BackColor = _t.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // intro
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // device list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // select-all row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // save/cancel

        root.Controls.Add(new Label
        {
            Text = "Tick the devices you want the app to use. Untick anything that isn't a fader " +
                   "(some controllers and gadgets share the same HID page and get picked up by mistake).",
            AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = _t.Subtle, Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);

        _list.BackColor = _t.CtlBg; _list.ForeColor = _t.Text; _list.Dock = DockStyle.Fill; _list.Margin = new Padding(0);
        _list.DrawItem += (_, e) => DrawRow(e);
        _list.MouseDown += (_, e) => Toggle(_list.IndexFromPoint(e.Location));
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) { Toggle(_list.SelectedIndex); e.Handled = true; } };
        foreach (var r in _rows) _list.Items.Add(r);
        root.Controls.Add(_list, 0, 1);

        var selRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        var all = MakeButton("Monitor all", false); all.Margin = new Padding(0, 0, 6, 0); all.Click += (_, _) => SetAll(true);
        var none = MakeButton("Ignore all", false); none.Click += (_, _) => SetAll(false);
        selRow.Controls.Add(all); selRow.Controls.Add(none);
        root.Controls.Add(selRow, 0, 2);

        var btnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        var save = MakeButton("Save", true); save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = MakeButton("Cancel", false); cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnRow.Controls.Add(save); btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 3);

        Controls.Add(root);
        CancelButton = cancel;
        if (_rows.Count > 0) _list.SelectedIndex = 0;

        Load += (_, _) =>
        {
            ApplyDark();
            ClientSize = new Size(LogicalToDeviceUnits(460),
                Math.Min(LogicalToDeviceUnits(460),
                    Math.Max(LogicalToDeviceUnits(300),
                        Screen.FromControl(this).WorkingArea.Height - LogicalToDeviceUnits(80))));
            // Two text lines plus padding, tracking the DPI-scaled font.
            _list.ItemHeight = _list.Font.Height * 2 + LogicalToDeviceUnits(14);
        };
    }

    void Toggle(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _rows[index].Monitored = !_rows[index].Monitored;
        _list.Invalidate();
    }

    void SetAll(bool on)
    {
        foreach (var r in _rows) r.Monitored = on;
        _list.Invalidate();
    }

    void DrawRow(DrawItemEventArgs e)
    {
        if (e.Index < 0) { e.DrawBackground(); return; }
        var r = _rows[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? _t.Accent : _t.CtlBg)) e.Graphics.FillRectangle(b, e.Bounds);
        Color fg = sel ? AccentText() : _t.Text;
        Color sub = sel ? AccentText() : _t.Subtle;

        int pad = LogicalToDeviceUnits(8);
        var boxRect = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, LogicalToDeviceUnits(22), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, r.Monitored ? "☑" : "☐", _list.Font, boxRect, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        int left = boxRect.Right + LogicalToDeviceUnits(4);
        int lineH = _list.Font.Height;
        int top = e.Bounds.Y + LogicalToDeviceUnits(5);

        // Line 1: device name, plus a badge for units that match our VID / are live.
        string badge = r.Connected ? "  · in use" : r.OurVid ? "  · fader device" : "";
        var nameRect = new Rectangle(left, top, e.Bounds.Right - left - pad, lineH);
        using (var bold = new Font(_list.Font, FontStyle.Bold))
        {
            TextRenderer.DrawText(e.Graphics, r.Name, bold, nameRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (badge.Length > 0)
            {
                int nameW = TextRenderer.MeasureText(e.Graphics, r.Name, bold, nameRect.Size, TextFormatFlags.NoPrefix).Width;
                var badgeRect = new Rectangle(left + nameW, top, nameRect.Width - nameW, lineH);
                TextRenderer.DrawText(e.Graphics, badge, _list.Font, badgeRect, r.Connected && !sel ? _t.Accent : sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        // Line 2: VID:PID · serial · match kind.
        var detailRect = new Rectangle(left, top + lineH, e.Bounds.Right - left - pad, lineH);
        TextRenderer.DrawText(e.Graphics, r.Detail, _list.Font, detailRect, sub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    RoundedButton MakeButton(string text, bool accent)
    {
        var b = new RoundedButton { Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(6, 0, 0, 0), Surround = _t.Window };
        if (accent) { b.BackColor = _t.Accent; b.ForeColor = AccentText(); b.FlatAppearance.BorderColor = _t.Accent; }
        else { b.BackColor = _t.CtlBg; b.ForeColor = _t.Text; b.FlatAppearance.BorderColor = _t.CtlBorder; }
        return b;
    }

    Color AccentText()
    {
        var a = _t.Accent;
        double lum = (0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255.0;
        return lum > 0.55 ? Color.FromArgb(0x10, 0x18, 0x12) : Color.White;
    }

    void ApplyDark()
    {
        int v = _t.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
        if (_list.IsHandleCreated) SetWindowTheme(_list.Handle, _t.Dark ? "DarkMode_Explorer" : null, null);
    }
}

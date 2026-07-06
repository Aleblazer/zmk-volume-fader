using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// A vertical scroll viewport with a custom, rounded (pill) thumb instead of the
/// squared native scrollbar. Add the scrolling content once via <see cref="SetContent"/>
/// (typically a Dock.Top / AutoSize panel); this positions it and paints the thumb.
/// Honors <see cref="Control.Padding"/> and routes the mouse wheel from anywhere
/// over the viewport (so hovering a combo scrolls rather than changing its value).
/// </summary>
internal sealed class RoundedScrollPanel : Panel, IMessageFilter
{
    Control? _content;
    int _offset;
    bool _laying, _drag, _hoverBar;
    int _dragY, _dragOff;

    public Color ThumbColor { get; set; } = Color.Gray;
    public Color ThumbHoverColor { get; set; } = Color.DarkGray;

    public RoundedScrollPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void SetContent(Control content)
    {
        _content = content;
        content.AutoSize = false;     // we size it explicitly from GetPreferredSize
        content.Left = 0; content.Top = 0;
        Controls.Add(content);
        Reflow();
    }

    int BarW => LogicalToDeviceUnits(9);
    int MinThumb => LogicalToDeviceUnits(28);
    // Viewport = client area minus padding.
    Rectangle Vp => DisplayRectangle;
    int ContentH => _content?.Height ?? 0;
    int MaxOff => Math.Max(0, ContentH - Vp.Height);
    bool ShowBar => MaxOff > 0;

    protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); Reflow(); }

    public void Reflow()
    {
        if (_content == null || _laying) return;
        _laying = true;
        // Two passes: measure at full width to decide whether a bar is needed,
        // then measure again at the reduced width.
        int full = Vp.Width;
        int h0 = _content.GetPreferredSize(new Size(full, 0)).Height;
        bool bar = h0 > Vp.Height;
        int w = Math.Max(1, full - (bar ? BarW + LogicalToDeviceUnits(2) : 0));
        int h = _content.GetPreferredSize(new Size(w, 0)).Height;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, h - Vp.Height));
        _content.Bounds = new Rectangle(Vp.Left, Vp.Top - _offset, w, h);
        _laying = false;
        Invalidate();
    }

    Rectangle ThumbRect()
    {
        if (!ShowBar) return Rectangle.Empty;
        int trackH = Vp.Height;
        int th = Math.Max(MinThumb, (int)((long)trackH * Vp.Height / ContentH));
        int ty = Vp.Top + (MaxOff == 0 ? 0 : (int)((long)(trackH - th) * _offset / MaxOff));
        int pad = LogicalToDeviceUnits(1);
        return new Rectangle(ClientSize.Width - BarW + pad, ty + pad, BarW - pad * 2, th - pad * 2);
    }

    void ScrollBy(int dy)
    {
        int n = Math.Clamp(_offset + dy, 0, MaxOff);
        if (n != _offset) { _offset = n; _content!.Top = Vp.Top - _offset; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!ShowBar) return;
        var r = ThumbRect();
        if (r.Width <= 0 || r.Height <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_hoverBar || _drag ? ThumbHoverColor : ThumbColor);
        using var p = RoundGfx.Round(r, r.Width / 2f);
        e.Graphics.FillPath(b, p);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (ShowBar && ThumbRect().Contains(e.Location)) { _drag = true; _dragY = e.Y; _dragOff = _offset; }
        else if (ShowBar && e.X >= ClientSize.Width - BarW)
            ScrollBy(e.Y < ThumbRect().Y ? -Vp.Height : Vp.Height);   // page up/down
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag)
        {
            int track = Vp.Height - ThumbRect().Height;
            if (track > 0)
            {
                _offset = Math.Clamp(_dragOff + (int)((long)(e.Y - _dragY) * MaxOff / track), 0, MaxOff);
                _content!.Top = Vp.Top - _offset;
                Invalidate();
            }
        }
        else
        {
            bool h = ShowBar && ThumbRect().Contains(e.Location);
            if (h != _hoverBar) { _hoverBar = h; Invalidate(); }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { if (_hoverBar) { _hoverBar = false; Invalidate(); } base.OnMouseLeave(e); }

    // Route the wheel from anywhere over the viewport (incl. child combos) to scroll.
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Application.AddMessageFilter(this); }
    protected override void OnHandleDestroyed(EventArgs e) { Application.RemoveMessageFilter(this); base.OnHandleDestroyed(e); }

    const int WM_MOUSEWHEEL = 0x020A;
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL || !ShowBar || !IsHandleCreated || !Visible || FindForm()?.Visible != true)
            return false;
        if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) return false;
        int delta = (short)((long)m.WParam >> 16 & 0xFFFF);
        ScrollBy(-delta);
        return true;
    }
}

static class RoundGfx
{
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        float d = rad * 2;
        var p = new GraphicsPath();
        if (d <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>Flat button with anti-aliased rounded corners. The true corners are
/// painted with <see cref="Surround"/> (the container colour) so they blend in
/// without needing a jagged clip Region. Lightens on hover, darkens on press.</summary>
internal sealed class RoundedButton : Button
{
    public Color Surround { get; set; } = Color.Black;
    public int Radius { get; set; } = 8;
    // Optional leading glyph, painted in the current ForeColor to the left of the
    // text. Receives (graphics, icon rect, colour). Null = text-only (centred).
    public Action<Graphics, Rectangle, Color>? DrawIcon { get; set; }
    public int IconSize { get; set; } = 14;
    bool _hover, _down;

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
    }

    // AutoSize measures text only; reserve room for the leading icon + gap so the
    // label isn't clipped.
    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = base.GetPreferredSize(proposedSize);
        if (DrawIcon != null) s.Width += IconSize + 6;
        return s;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Surround);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = RoundGfx.Round(r, Radius);

        var fill = BackColor;
        if (_down) fill = RoundGfx.Blend(fill, Color.Black, 0.14f);
        else if (_hover) fill = RoundGfx.Blend(fill, Color.White, 0.10f);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);

        var border = FlatAppearance.BorderColor;
        if (border.A > 0 && border != BackColor)
            using (var p = new Pen(border, 1f)) g.DrawPath(p, path);

        if (DrawIcon == null)
        {
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        // Icon + text centred as a group.
        const int gap = 6;
        var ts = TextRenderer.MeasureText(g, Text, Font, new Size(int.MaxValue, Height),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int total = IconSize + gap + ts.Width;
        int x = Math.Max(2, (Width - total) / 2);
        DrawIcon(g, new Rectangle(x, (Height - IconSize) / 2, IconSize, IconSize), ForeColor);
        var tr = new Rectangle(x + IconSize + gap, 0, Width - (x + IconSize + gap), Height);
        TextRenderer.DrawText(g, Text, Font, tr, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>DropDownList combo whose closed box is custom-painted as a rounded,
/// themed rect with a chevron (the popup list stays native, themed via
/// SetWindowTheme). Items can still be owner-drawn via the DrawItem event.</summary>
internal sealed class RoundedComboBox : ComboBox
{
    public Color Surround { get; set; } = Color.Black;
    public Color BoxColor { get; set; } = Color.White;
    public Color BorderColor { get; set; } = Color.Gray;
    public Color ChevronColor { get; set; } = Color.Gray;
    public int Radius { get; set; } = 8;
    // Force a taller control than the font-derived default (a DropDownList clamps
    // its own height in SetBoundsCore, so we override it via WM_WINDOWPOSCHANGING).
    // 0 = let the combo size itself.
    public int DesiredHeight { get; set; }
    // Shown (in PlaceholderColor) when nothing is selected.
    public string? Placeholder { get; set; }
    public Color PlaceholderColor { get; set; } = Color.Gray;
    // Optional leading icon for the selected item, painted in the closed box.
    // Receives (graphics, icon rect, selected item, text colour). List rows are
    // owner-drawn separately via the DrawItem event.
    public Action<Graphics, Rectangle, object?, Color>? DrawLeadingIcon { get; set; }

    const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_WINDOWPOSCHANGING = 0x0046;
    const uint SWP_NOSIZE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc; public bool fErase; public RECT rcPaint; public bool fRestore;
        public bool fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    public RoundedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        // Allow a custom (taller) control height — the closed box is fully
        // owner-painted, and a DropDownList otherwise snaps to the font height and
        // clips descenders at high DPI.
        IntegralHeight = false;
    }

    // Report the forced height to the layout engine too, so the row/card that
    // holds this combo is sized tall enough and doesn't clip its bottom.
    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = base.GetPreferredSize(proposedSize);
        if (DesiredHeight > 0) s.Height = DesiredHeight;
        return s;
    }

    protected override void WndProc(ref Message m)
    {
        // Override the DropDownList's self-imposed height clamp.
        if (m.Msg == WM_WINDOWPOSCHANGING && DesiredHeight > 0)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            if ((wp.flags & SWP_NOSIZE) == 0 && wp.cy != DesiredHeight)
            {
                wp.cy = DesiredHeight;
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
            base.WndProc(ref m);
            return;
        }
        if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
        if (m.Msg == WM_PAINT)
        {
            IntPtr hdc = BeginPaint(Handle, out var ps);
            try
            {
                // Paint straight to the (opaque) DC so ClearType text stays crisp;
                // an ARGB back-buffer makes dark-on-light text look bold/fringed.
                using var g = Graphics.FromHdc(hdc);
                PaintClosed(g);
            }
            finally { EndPaint(Handle, ref ps); }
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    void PaintClosed(Graphics g)
    {
        // Paint into the actual client area — the control Height can exceed
        // ClientSize.Height, and centring on Height then drops text below the
        // painted region and clips it.
        int w = ClientSize.Width, h = ClientSize.Height;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Surround);
        var r = new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f);
        using (var path = RoundGfx.Round(r, Radius))
        {
            using (var b = new SolidBrush(BoxColor)) g.FillPath(b, path);
            using (var p = new Pen(BorderColor, 1f)) g.DrawPath(p, path);
        }

        string text = GetItemText(SelectedItem) ?? string.Empty;
        Color textColor = ForeColor;
        if (SelectedIndex < 0 && string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(Placeholder))
        {
            text = Placeholder!;
            textColor = PlaceholderColor;
        }
        int textLeft = LogicalToDeviceUnits(9);
        if (DrawLeadingIcon != null && SelectedIndex >= 0 && SelectedItem != null)
        {
            int isz = LogicalToDeviceUnits(18);
            var ir = new Rectangle(LogicalToDeviceUnits(8), (h - isz) / 2, isz, isz);
            DrawLeadingIcon(g, ir, SelectedItem, textColor);
            textLeft = ir.Right + LogicalToDeviceUnits(6);
        }
        var textRect = new Rectangle(textLeft, 0, w - textLeft - LogicalToDeviceUnits(22), h);
        TextRenderer.DrawText(g, text, Font, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        float cxv = w - LogicalToDeviceUnits(15), cyv = h / 2f;
        float ax = LogicalToDeviceUnits(4), ay = LogicalToDeviceUnits(2);
        using var cp = new Pen(ChevronColor, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(cp, new[] { new PointF(cxv - ax, cyv - ay), new PointF(cxv, cyv + ay + 0.5f), new PointF(cxv + ax, cyv - ay) });
    }
}

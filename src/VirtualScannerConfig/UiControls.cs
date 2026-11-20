using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MbfTwain.VirtualScannerConfig;

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel() => DoubleBuffered = true;
}

internal sealed class StableScrollPanel : Panel
{
    public StableScrollPanel() => DoubleBuffered = true;

    protected override void OnLayout(LayoutEventArgs e)
    {
        // Reserve the scrollbar's width before it appears, so background
        // imports cannot change the wrapping width of the thumbnail grid.
        int width = Math.Max(1, Width - SystemInformation.VerticalScrollBarWidth);
        foreach (Control child in Controls)
        {
            if (child is not BufferedFlowLayoutPanel || child.MinimumSize.Width == width) continue;
            child.MinimumSize = new Size(width, 0);
            child.MaximumSize = new Size(width, 0);
        }
        base.OnLayout(e);
    }
}

internal sealed class ActionButton : Button
{
    private bool hovered;
    private bool pressed;

    public ActionButton()
    {
        FlatStyle = FlatStyle.Flat;
        BackColor = Color.FromArgb(248, 250, 246);
        ForeColor = Color.FromArgb(40, 64, 53);
        FlatAppearance.BorderColor = Color.FromArgb(210, 217, 206);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        hovered = pressed = false;
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; base.OnMouseDown(e); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; base.OnMouseUp(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color background = !Enabled ? Color.FromArgb(233, 236, 231)
            : pressed ? ControlPaint.Dark(BackColor, .08f)
            : hovered ? ControlPaint.Dark(BackColor, .025f) : BackColor;
        Color foreground = Enabled ? ForeColor : Color.FromArgb(117, 127, 119);
        e.Graphics.Clear(background);
        if (FlatAppearance.BorderSize > 0)
        {
            using var border = new Pen(Enabled ? FlatAppearance.BorderColor : Color.FromArgb(215, 221, 213));
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), foreground, background);
    }
}

internal sealed class BusyIndicator : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 80 };
    private int angle;
    private bool active;

    public BusyIndicator()
    {
        Size = new Size(24, 24);
        ForeColor = Color.FromArgb(29, 111, 84);
        TabStop = false;
        AccessibleName = "正在加载";
        SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        timer.Tick += (_, _) => { angle = (angle + 24) % 360; Invalidate(); };
    }

    [DefaultValue(false)]
    internal bool Active
    {
        get => active;
        set { active = value; UpdateTimer(); Invalidate(); }
    }

    private void UpdateTimer() => timer.Enabled = active && Visible && IsHandleCreated && !IsDisposed;
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateTimer(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateTimer(); }
    protected override void OnHandleDestroyed(EventArgs e) { timer.Stop(); base.OnHandleDestroyed(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!active) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Max(1, Math.Min(Width, Height) - 6);
        var bounds = new RectangleF((Width - size) / 2, (Height - size) / 2, size, size);
        using var track = new Pen(Color.FromArgb(42, ForeColor), 2.5f);
        using var arc = new Pen(ForeColor, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawEllipse(track, bounds);
        e.Graphics.DrawArc(arc, bounds, angle, 245);
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

internal sealed class StateCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        e.Graphics.Clear(BackColor);
        var state = Checked ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedDisabled
            : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled;
        Size glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
        CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(0, (Height - glyph.Height) / 2), state);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(glyph.Width + 4, 0, Math.Max(0, Width - glyph.Width - 4), Height),
            Color.FromArgb(117, 127, 119), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

internal sealed class StateLinkLabel : LinkLabel
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(117, 127, 119),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

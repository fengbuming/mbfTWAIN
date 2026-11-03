using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MbfTwain.VirtualScannerConfig;

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

internal sealed class WaitOverlay : Panel
{
    private readonly TableLayoutPanel surface;
    private readonly BusyIndicator indicator = new() { Anchor = AnchorStyles.None, Size = new Size(32, 32) };
    private readonly Label title = new() { AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(8) };
    private readonly Label detail = new() { AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(8, 0, 8, 10), MaximumSize = new Size(390, 0) };
    private readonly ProgressBar progress = new() { Width = 280, Height = 6, Anchor = AnchorStyles.None, Maximum = 1000 };
    private readonly ActionButton cancel = new() { Text = "取消加载", Width = 112, Height = 32 };
    private readonly ActionButton abort = new() { Text = "中止加载", Width = 112, Height = 32 };
    private Bitmap? backdrop;
    private bool showing;

    internal event EventHandler? CancelRequested;
    internal event EventHandler? AbortRequested;
    internal bool IsBusy => indicator.Active;
    internal bool IsShowing => showing;

    public WaitOverlay()
    {
        Dock = DockStyle.Fill;
        Visible = false;
        TabStop = false;
        BackColor = Color.FromArgb(247, 249, 245);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        surface = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 5, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.FromArgb(255, 255, 252), Padding = new Padding(20, 14, 20, 14),
        };
        surface.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int row = 0; row < 5; row++) surface.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        title.Font = new Font(Font, FontStyle.Bold);
        detail.ForeColor = Color.FromArgb(94, 104, 97);
        detail.TextAlign = ContentAlignment.MiddleCenter;
        surface.Controls.Add(indicator, 0, 0);
        surface.Controls.Add(title, 0, 1);
        surface.Controls.Add(detail, 0, 2);
        surface.Controls.Add(progress, 0, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 8, 0, 0) };
        actions.Controls.AddRange([cancel, abort]);
        surface.Controls.Add(actions, 0, 4);
        Controls.Add(surface);
        cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        abort.Click += (_, _) => AbortRequested?.Invoke(this, EventArgs.Empty);
        surface.SizeChanged += (_, _) => CenterSurface();
    }

    internal void ShowLoading(Control? content, string caption, string description, int completed = 0, int total = 0, bool allowAbort = true)
    {
        if (!showing && content is { Width: > 0, Height: > 0 })
        {
            backdrop?.Dispose();
            backdrop = new Bitmap(content.Width, content.Height);
            content.DrawToBitmap(backdrop, new Rectangle(Point.Empty, content.Size));
        }
        title.Text = caption;
        showing = true;
        detail.Text = description;
        indicator.Active = true;
        abort.Visible = allowAbort;
        progress.Visible = total > 0;
        progress.Value = total <= 0 ? 0 : (int)Math.Clamp(completed * 1000L / total, 0, 1000);
        Visible = true;
        BringToFront();
        CenterSurface();
    }

    internal void Dismiss()
    {
        indicator.Active = false;
        showing = false;
        Visible = false;
        backdrop?.Dispose();
        backdrop = null;
    }

    private void CenterSurface() => surface.Location = new Point(Math.Max(0, (Width - surface.Width) / 2), Math.Max(0, (Height - surface.Height) / 2));
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (surface is not null) CenterSurface(); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (backdrop is null) return;
        e.Graphics.DrawImage(backdrop, ClientRectangle);
        using var veil = new SolidBrush(Color.FromArgb(224, BackColor));
        e.Graphics.FillRectangle(veil, ClientRectangle);
    }
    protected override void Dispose(bool disposing) { if (disposing) backdrop?.Dispose(); base.Dispose(disposing); }
}

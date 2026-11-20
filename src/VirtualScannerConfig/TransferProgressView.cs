using System;
using System.Drawing;
using System.Windows.Forms;

namespace MbfTwain.VirtualScannerConfig;

// Lives in the queue viewport so loading, transfer and recovery share one window.
internal sealed class TransferProgressView : UserControl
{
    private readonly Label titleLabel = new();
    private readonly Label detailLabel = new();
    private readonly ProgressBar progressBar = new();
    private readonly Button cancelButton = new ActionButton();
    private readonly Button continueButton = new ActionButton();
    private readonly Button abortButton = new ActionButton();
    private readonly Button skipButton = new ActionButton();
    private readonly Button replaceButton = new ActionButton();
    private readonly BusyIndicator busyIndicator = new();
    private bool paused;
    private bool imageFailed;
    private string? issueMessage;
    private bool readingImage;
    private bool waiting;
    private int completed;
    private int total = 1;

    public event EventHandler? CancelRequested;
    public event EventHandler? ContinueRequested;
    public event EventHandler? AbortRequested;
    public event EventHandler? SkipRequested;
    public event EventHandler? ReplaceRequested;

    public TransferProgressView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(255, 255, 252);
        DoubleBuffered = true;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 620));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(18, 6, 18, 6), Margin = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 32, 26, 64, 38, 38 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleLabel.Dock = DockStyle.Fill;
        titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        titleLabel.Font = new Font(Font, FontStyle.Bold);
        titleLabel.AutoEllipsis = true;
        heading.Controls.Add(busyIndicator, 0, 0);
        heading.Controls.Add(titleLabel, 1, 0);
        layout.Controls.Add(heading, 0, 0);
        progressBar.Dock = DockStyle.Fill;
        progressBar.Maximum = 1000;
        progressBar.Margin = new Padding(0, 2, 0, 10);
        layout.Controls.Add(progressBar, 0, 1);
        detailLabel.Dock = DockStyle.Fill;
        detailLabel.AutoEllipsis = true;
        detailLabel.ForeColor = Color.FromArgb(94, 104, 97);
        detailLabel.Margin = new Padding(0, 4, 0, 8);
        layout.Controls.Add(detailLabel, 0, 2);

        cancelButton.Text = "暂停送图";
        continueButton.Text = "继续送图";
        abortButton.Text = "中止本次扫描";
        skipButton.Text = "跳过此图继续";
        replaceButton.Text = "更换此图";
        foreach (Button button in new[] { cancelButton, continueButton, abortButton, skipButton, replaceButton })
        {
            button.Size = new Size(124, 32);
            button.Margin = new Padding(8, 2, 0, 4);
        }
        continueButton.BackColor = Color.FromArgb(29, 111, 84);
        continueButton.ForeColor = Color.White;
        continueButton.FlatAppearance.BorderSize = 0;
        abortButton.ForeColor = Color.FromArgb(151, 68, 49);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = new Padding(0),
        };
        actions.Controls.AddRange([continueButton, cancelButton, abortButton]);
        var recovery = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = new Padding(0),
        };
        recovery.Controls.AddRange([skipButton, replaceButton]);
        layout.Controls.Add(actions, 0, 3);
        layout.Controls.Add(recovery, 0, 4);
        cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        continueButton.Click += (_, _) => ContinueRequested?.Invoke(this, EventArgs.Empty);
        abortButton.Click += (_, _) => AbortRequested?.Invoke(this, EventArgs.Empty);
        skipButton.Click += (_, _) => SkipRequested?.Invoke(this, EventArgs.Empty);
        replaceButton.Click += (_, _) => ReplaceRequested?.Invoke(this, EventArgs.Empty);
        root.Controls.Add(layout, 1, 1);
        Controls.Add(root);
        Render();
    }

    public void UpdateProgress(uint revision, int completedImages, int totalImages, bool waitingForTwain)
    {
        total = Math.Max(1, totalImages);
        completed = Math.Clamp(completedImages, 0, total);
        waiting = waitingForTwain;
        Render();
    }

    public void SetPaused(bool value, string? message = null, bool imageFailed = false)
    {
        paused = value;
        this.imageFailed = value && imageFailed;
        if (value) readingImage = false;
        issueMessage = value ? message ?? issueMessage : null;
        Render();
    }

    public void SetReadingImage(bool reading)
    {
        if (readingImage == reading) return;
        readingImage = reading;
        Render();
    }

    private void Render()
    {
        bool complete = completed >= total;
        titleLabel.Text = complete ? "送图完成，正在收尾…"
            : paused ? "送图已暂停"
            : readingImage ? "正在读取当前图片…"
            : waiting ? "等待扫描程序取图…"
            : $"正在送出第 {Math.Min(completed + 1, total)}/{total} 页…";
        string hint = paused ? issueMessage ?? "继续将从未完成的页面开始。"
            : readingImage ? "正在准备图片，可随时暂停或中止。"
            : complete ? "正在确认扫描结果。" : "可以暂停后继续，或中止并保留尚未送出的图片。";
        detailLabel.Text = $"已送出 {completed}/{total} 页\n{hint}";
        titleLabel.ForeColor = imageFailed ? Color.FromArgb(151, 86, 25) : Color.FromArgb(33, 43, 37);
        detailLabel.BackColor = imageFailed ? Color.FromArgb(255, 246, 225) : BackColor;
        progressBar.Value = (int)(completed * 1000L / total);
        ProgressBarStyle style = waiting && !paused && !complete ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (progressBar.Style != style) progressBar.Style = style;
        progressBar.MarqueeAnimationSpeed = style == ProgressBarStyle.Marquee ? 30 : 0;
        busyIndicator.Active = !paused && !complete;
        cancelButton.Enabled = !paused && !complete;
        continueButton.Enabled = paused && !complete;
        continueButton.Text = imageFailed ? "重试此图" : "继续送图";
        abortButton.Enabled = !complete;
        skipButton.Visible = replaceButton.Visible = paused && imageFailed && !complete;
    }
}

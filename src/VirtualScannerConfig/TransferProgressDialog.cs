using System;
using System.Drawing;
using System.Windows.Forms;

namespace MbfTwain.VirtualScannerConfig;

internal sealed class TransferProgressDialog : Form
{
    private readonly Label titleLabel = new();
    private readonly Label detailLabel = new();
    private readonly ProgressBar progressBar = new();
    private readonly Button cancelButton = new();
    private readonly Button continueButton = new();
    private readonly Button abortButton = new();
    private readonly Button skipButton = new();
    private readonly Button replaceButton = new();
    private bool paused;
    private string? issueMessage;

    public event EventHandler? CancelRequested;
    public event EventHandler? ContinueRequested;
    public event EventHandler? AbortRequested;
    public event EventHandler? SkipRequested;
    public event EventHandler? ReplaceRequested;

    public TransferProgressDialog()
    {
        Text = "正在送图";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 232);
        BackColor = Color.FromArgb(255, 255, 252);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18, 16, 18, 14),
            BackColor = BackColor,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        titleLabel.AutoSize = true;
        titleLabel.Font = new Font(Font, FontStyle.Bold);
        titleLabel.ForeColor = Color.FromArgb(33, 43, 37);
        titleLabel.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(titleLabel, 0, 0);

        progressBar.Dock = DockStyle.Top;
        progressBar.Minimum = 0;
        progressBar.Maximum = 1000;
        progressBar.Height = 22;
        progressBar.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(progressBar, 0, 1);

        detailLabel.AutoSize = true;
        detailLabel.MaximumSize = new Size(580, 86);
        detailLabel.ForeColor = Color.FromArgb(94, 104, 97);
        detailLabel.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(detailLabel, 0, 2);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0),
        };
        cancelButton.Text = "取消（可继续）";
        cancelButton.Width = 118;
        cancelButton.Height = 30;
        cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        buttonPanel.Controls.Add(cancelButton);
        continueButton.Text = "继续";
        continueButton.Width = 82;
        continueButton.Height = 30;
        continueButton.Enabled = false;
        continueButton.Click += (_, _) => ContinueRequested?.Invoke(this, EventArgs.Empty);
        buttonPanel.Controls.Add(continueButton);
        abortButton.Text = "中止本次扫描";
        abortButton.Width = 118;
        abortButton.Height = 30;
        abortButton.Click += (_, _) => AbortRequested?.Invoke(this, EventArgs.Empty);
        buttonPanel.Controls.Add(abortButton);
        skipButton.Text = "跳过此图继续";
        replaceButton.Text = "更换此图";
        foreach (Button button in new[] { skipButton, replaceButton })
        {
            button.Width = 104;
            button.Height = 30;
            button.Visible = false;
            buttonPanel.Controls.Add(button);
        }
        skipButton.Click += (_, _) => SkipRequested?.Invoke(this, EventArgs.Empty);
        replaceButton.Click += (_, _) => ReplaceRequested?.Invoke(this, EventArgs.Empty);
        layout.Controls.Add(buttonPanel, 0, 3);

        Controls.Add(layout);
    }

    public void UpdateProgress(uint revision, int completedImages, int totalImages, bool waitingForTwain)
    {
        int normalizedTotal = Math.Max(1, totalImages);
        int normalizedCompleted = Math.Clamp(completedImages, 0, normalizedTotal);
        bool complete = normalizedCompleted >= normalizedTotal;

        titleLabel.Text = waitingForTwain
            ? "等待 TWAIN 程序取图..."
            : complete
                ? "送图完成，正在收尾..."
                : $"正在送出第 {Math.Min(normalizedCompleted + 1, normalizedTotal)}/{normalizedTotal} 页...";
        detailLabel.Text = $"会话 {revision} · 已送出 {normalizedCompleted}/{normalizedTotal} 页";
        progressBar.Value = complete
            ? progressBar.Maximum
            : (int)Math.Round(normalizedCompleted * (progressBar.Maximum / (double)normalizedTotal));
        cancelButton.Enabled = !complete;
    }

    public void SetPaused(bool value, string? message = null, bool imageFailed = false)
    {
        bool changed = paused != value;
        paused = value;
        cancelButton.Enabled = !value;
        continueButton.Enabled = value;
        continueButton.Text = imageFailed ? "重试此图" : "继续";
        skipButton.Visible = value && imageFailed;
        replaceButton.Visible = value && imageFailed;
        if (value)
        {
            titleLabel.Text = "送图已暂停";
            if (message is not null) issueMessage = message;
            if (issueMessage is not null) detailLabel.Text = issueMessage;
            else if (changed) detailLabel.Text += " · 继续将从未完成的页面开始";
        }
        else
        {
            issueMessage = null;
            if (changed) titleLabel.Text = "正在继续送图…";
        }
    }
}

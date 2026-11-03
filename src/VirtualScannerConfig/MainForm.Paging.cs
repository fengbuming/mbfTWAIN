using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MbfTwain.VirtualScannerConfig;

public sealed partial class MainForm
{
    private const int QueuePageSize = 12;
    private int queuePageIndex;
    private readonly Button previousQueuePageButton = new ActionButton();
    private readonly Button nextQueuePageButton = new ActionButton();
    private readonly NumericUpDown queuePageInput = new();
    private readonly Label queuePageLabel = new();
    private bool updatingQueuePage;

    private Control BuildQueuePagination()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0),
        };
        previousQueuePageButton.Text = "上一页";
        nextQueuePageButton.Text = "下一页";
        foreach (Button button in new[] { previousQueuePageButton, nextQueuePageButton })
        {
            button.AutoSize = true;
            button.Height = 28;
        }

        queuePageInput.Minimum = 1;
        queuePageInput.Maximum = 1;
        queuePageInput.Width = 68;
        queuePageInput.Margin = new Padding(8, 4, 8, 0);
        queuePageLabel.AutoSize = true;
        queuePageLabel.ForeColor = MutedTextColor;
        queuePageLabel.Margin = new Padding(8, 6, 0, 0);
        panel.Controls.AddRange([previousQueuePageButton, queuePageInput, nextQueuePageButton, queuePageLabel]);
        previousQueuePageButton.Click += (_, _) => ChangeQueuePage(queuePageIndex - 1);
        nextQueuePageButton.Click += (_, _) => ChangeQueuePage(queuePageIndex + 1);
        queuePageInput.ValueChanged += (_, _) =>
        {
            if (!updatingQueuePage) ChangeQueuePage((int)queuePageInput.Value - 1);
        };
        return panel;
    }

    private void ChangeQueuePage(int pageIndex)
    {
        int count;
        lock (stateLock) count = selectedImages.Count;
        queuePageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, (count - 1) / QueuePageSize));
        hoveredImageIndex = -1;
        thumbnailStrip.AutoScrollPosition = Point.Empty;
        RefreshQueueVisuals();
    }

    private void UpdateQueuePagination(int count)
    {
        int pages = Math.Max(1, (count + QueuePageSize - 1) / QueuePageSize);
        queuePageIndex = Math.Clamp(queuePageIndex, 0, pages - 1);
        updatingQueuePage = true;
        queuePageInput.Maximum = pages;
        queuePageInput.Value = queuePageIndex + 1;
        updatingQueuePage = false;
        previousQueuePageButton.Enabled = queuePageIndex > 0;
        nextQueuePageButton.Enabled = queuePageIndex + 1 < pages;
        int first = count == 0 ? 0 : queuePageIndex * QueuePageSize + 1;
        int last = Math.Min(count, (queuePageIndex + 1) * QueuePageSize);
        queuePageLabel.Text = $"第 {queuePageIndex + 1}/{pages} 页 · 图片 {first}–{last}/{count} · 每页 {QueuePageSize} 张";
    }
}

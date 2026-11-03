using System;
using System.Drawing;
using System.Windows.Forms;

namespace MbfTwain.VirtualScannerConfig;

public sealed partial class MainForm
{
    private readonly WaitOverlay queueWaitOverlay = new();
    private readonly BusyIndicator imageLoadingIndicator = new() { Margin = new Padding(0, 0, 6, 0) };
    private readonly Panel queueViewport = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
    private bool disposingImageUi;
    private bool queueUiInitialized;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && rememberedImageRestoreCancellation is not null)
        {
            StopImageLoading(showMessage: true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void InitializeQueueBusyState()
    {
        queueViewport.Controls.Add(thumbnailStrip);
        queueViewport.Controls.Add(queueWaitOverlay);
        queueWaitOverlay.CancelRequested += (_, _) => StopImageLoading(showMessage: true);
        queueWaitOverlay.AbortRequested += (_, _) => AbortImageImport();
        queueUiInitialized = true;
        UpdateQueueBusyState();
    }

    private void AbortImageImport()
    {
        StopImageLoading(showMessage: false);
        imageLoadingPaused = true;
        imageLoadingLabel.Text = "本次加载已中止，已加入的图片和记忆记录已保留。";
        UpdateImageLoadingActions();
    }

    private void UpdateQueueBusyState()
    {
        if (!queueUiInitialized || disposingImageUi || IsDisposed || Disposing) return;
        bool importing = rememberedImageRestoreCancellation is not null;
        bool editing = !importing && !scanRequested;
        int count;
        lock (stateLock) count = selectedImages.Count;
        thumbnailStrip.Enabled = editing;
        clearImagesLink.Enabled = editing && count > 0;
        rememberImagesCheckBox.Enabled = editing;
        settingsButton.Enabled = editing;
        if (addTileButton is not null) addTileButton.Enabled = editing;
        queuePageInput.Enabled = editing && count > QueuePageSize;
        previousQueuePageButton.Enabled = editing && queuePageIndex > 0;
        nextQueuePageButton.Enabled = editing && (queuePageIndex + 1) * QueuePageSize < count;
        triggerScanButton.Enabled = editing;
        triggerScanButton.Text = importing ? "正在加载…" : "开始扫描";
        imageLoadingIndicator.Active = !imageLoadingPaused && (importing || pendingThumbnailLoads > 0);
        imageLoadingIndicator.Visible = imageLoadingIndicator.Active;
        if (importing)
        {
            queueWaitOverlay.ShowLoading(thumbnailStrip, importIsRemembered ? "正在恢复图片队列" : "正在加入图片",
                $"已加入 {nextImportOffset}/{pendingImageImport?.Length ?? nextImportOffset} 张 · 可以取消后继续",
                nextImportOffset, pendingImageImport?.Length ?? nextImportOffset);
            thumbnailStrip.Visible = false;
        }
        else
        {
            queueWaitOverlay.Dismiss();
            thumbnailStrip.Visible = true;
        }
    }
}

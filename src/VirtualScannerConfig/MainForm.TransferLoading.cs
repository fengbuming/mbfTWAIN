using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MbfTwain.VirtualScannerConfig.Ipc;

namespace MbfTwain.VirtualScannerConfig;

public sealed partial class MainForm
{
    private readonly TransferImageStager transferImageStager;
    private bool transferPaused;
    private uint activeScanRevision;
    private int transferCompletedImages;
    private readonly HashSet<int> skippedTransferIndices = [];
    private int failedTransferImageIndex = -1;
    private Form? transferSummaryDialog;
    private bool exitAfterTransferSummary;

    private void TrackTransferProgress(uint scanRevision, int completedImages, int totalImages)
    {
        // Record progress before acknowledging the pipe message so an Abort
        // click cannot overtake an already delivered page's queued UI update.
        lock (stateLock)
        {
            if (scanRequested && scanRevision == activeScanRevision)
                transferCompletedImages = Math.Max(transferCompletedImages,
                    Math.Clamp(completedImages, 0, Math.Min(totalImages, selectedImages.Count)));
        }
    }

    private ScannerStateSnapshot GetPageSnapshot(int offset, int count)
    {
        lock (stateLock)
        {
            return new ScannerStateSnapshot(revision, duplexEnabled, pixelType, paperSize,
                scanDpi, scanDpi, transferBufferDelayMilliseconds, scanRequested,
                selectedImages.Skip(offset).Take(count).ToArray(), selectedImages.Count, offset, transferPaused, true);
        }
    }

    private string GetTransferImageResponse(uint requestedRevision, int index)
    {
        string source;
        lock (stateLock)
        {
            if (!scanRequested || requestedRevision != activeScanRevision || index < 0 || index >= selectedImages.Count)
                return "ERR stale-session\n";
            if (transferPaused) return "OK WAIT\n";
            if (skippedTransferIndices.Contains(index)) return "OK SKIP\n";
            source = selectedImages[index].Path;
        }
        return transferImageStager.Request(requestedRevision, index, source);
    }

    private void PauseTransfer(string? message = null)
    {
        lock (stateLock)
        {
            if (!scanRequested) return;
            transferPaused = true;
        }
        transferImageStager.Pause();
        transferProgressDialog?.SetPaused(true, message, failedTransferImageIndex >= 0);
    }

    private void ResumeTransfer()
    {
        transferImageStager.Resume();
        lock (stateLock)
        {
            if (!scanRequested) return;
            transferPaused = false;
            failedTransferImageIndex = -1;
        }
        transferProgressDialog?.SetPaused(false);
    }

    private void TransferImageReadFailed(uint scanRevision, int index, string message)
    {
        lock (stateLock)
        {
            if (!scanRequested || activeScanRevision != scanRevision) return;
            failedTransferImageIndex = index;
        }
        PauseTransfer(message);
    }

    private void SkipFailedTransferImage()
    {
        lock (stateLock)
        {
            if (!scanRequested || !transferPaused || failedTransferImageIndex < 0) return;
            skippedTransferIndices.Add(failedTransferImageIndex);
            DiagnosticsLog.Write("UI", $"User skipped image index={failedTransferImageIndex} path={selectedImages[failedTransferImageIndex].Path}");
        }
        ResumeTransfer();
    }

    private void ReplaceFailedTransferImage()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*",
            Title = "选择替代图片（只替换本次扫描的此张图片）",
        };
        if (dialog.ShowDialog(transferProgressDialog) == DialogResult.OK) ReplaceFailedTransferImageWith(dialog.FileName);
    }

    private void ReplaceFailedTransferImageWith(string path)
    {
        lock (stateLock)
        {
            if (!scanRequested || !transferPaused || failedTransferImageIndex < 0) return;
            selectedImages[failedTransferImageIndex] = selectedImages[failedTransferImageIndex] with { Path = Path.GetFullPath(path) };
        }
        ResumeTransfer();
    }

    private void ShowTransferSummary(string[] skippedPaths)
    {
        var dialog = new Form
        {
            Text = "扫描完成 · 跳过图片提醒", StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(620, 300), MinimumSize = new Size(500, 240),
        };
        var description = new Label
        {
            Dock = DockStyle.Top, Height = 54, Padding = new Padding(12),
            Text = $"本次已送出 {Math.Max(0, transferCompletedImages - skippedPaths.Length)} 张，按你的选择跳过 {skippedPaths.Length} 张。原记忆记录已保留。",
        };
        var paths = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Text = string.Join(Environment.NewLine, skippedPaths),
        };
        var close = new Button { Dock = DockStyle.Bottom, Height = 36, Text = "知道了" };
        close.Click += (_, _) => dialog.Close();
        dialog.Controls.Add(paths);
        dialog.Controls.Add(description);
        dialog.Controls.Add(close);
        transferSummaryDialog = dialog;
        dialog.FormClosed += (_, _) =>
        {
            transferSummaryDialog = null;
            if (exitAfterTransferSummary && !IsDisposed && !Disposing) RequestExit();
        };
        dialog.Show();
    }
}

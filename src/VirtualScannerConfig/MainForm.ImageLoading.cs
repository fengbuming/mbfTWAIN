using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MbfTwain.VirtualScannerConfig.Ipc;

namespace MbfTwain.VirtualScannerConfig;

public sealed partial class MainForm
{
    private readonly CancellationTokenSource imageLoadingLifetime = new();
    private readonly Dictionary<ScannerImageSelection, ThumbnailCacheKey> loadedThumbnailKeys = [];
    private readonly Label imageLoadingLabel = new();
    private readonly LinkLabel cancelImageLoadingLink = new();
    private readonly LinkLabel retryImageLoadingLink = new();
    private readonly Button triggerScanButton = new ActionButton();
    private CancellationTokenSource? rememberedImageRestoreCancellation;
    private int pendingThumbnailLoads;
    private int failedThumbnailCount;
    private ScannerImageSelection[]? pendingImageImport;
    private int nextImportOffset;
    private bool importIsRemembered;
    private bool imageLoadingPaused;
    private bool scanAfterImageImport;
    private readonly LinkLabel abortImageLoadingLink = new();

    private Control BuildImageLoadingStatus()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0), WrapContents = true,
        };
        imageLoadingLabel.AutoSize = true;
        panel.Controls.Add(imageLoadingIndicator);
        imageLoadingLabel.MaximumSize = new Size(480, 0);
        imageLoadingLabel.ForeColor = MutedTextColor;
        imageLoadingLabel.Margin = new Padding(0, 2, 10, 0);
        panel.Controls.Add(imageLoadingLabel);
        foreach (LinkLabel link in new[] { cancelImageLoadingLink, retryImageLoadingLink, abortImageLoadingLink })
        {
            link.AutoSize = true;
            link.LinkColor = Color.FromArgb(38, 100, 171);
            link.Margin = new Padding(0, 2, 10, 0);
            link.Visible = false;
            panel.Controls.Add(link);
        }

        cancelImageLoadingLink.Text = "取消加载";
        cancelImageLoadingLink.LinkClicked += (_, _) => StopImageLoading(showMessage: true);
        retryImageLoadingLink.LinkClicked += (_, _) => RetryImageLoading();
        abortImageLoadingLink.Text = "中止加载";
        abortImageLoadingLink.LinkClicked += (_, _) => AbortImageImport();
        return panel;
    }

    private void UpdateImageLoadingActions()
    {
        bool importing = rememberedImageRestoreCancellation is not null;
        cancelImageLoadingLink.Visible = !imageLoadingPaused && (importing || pendingThumbnailLoads > 0);
        retryImageLoadingLink.Text = imageLoadingPaused || pendingImageImport is not null ? "继续加载" : "重试本页";
        retryImageLoadingLink.Visible = !importing && pendingThumbnailLoads == 0 &&
            (imageLoadingPaused || pendingImageImport is not null || failedThumbnailCount > 0);
        abortImageLoadingLink.Visible = importing || pendingImageImport is not null || pendingThumbnailLoads > 0;
        triggerScanButton.Enabled = !importing;
        UpdateQueueBusyState();
    }

    private void StopImageLoading(bool showMessage)
    {
        CancellationTokenSource? operation = rememberedImageRestoreCancellation;
        rememberedImageRestoreCancellation = null;
        operation?.Cancel();
        thumbnailLoadCancellation.Cancel();
        scanAfterImageImport = false;
        imageLoadingPaused = showMessage;
        if (!showMessage)
        {
            pendingImageImport = null;
            nextImportOffset = 0;
        }

        imageLoadingLabel.Text = !showMessage ? string.Empty : pendingImageImport is null
            ? "已取消本页预览，图片队列已保留；可继续加载或开始扫描。"
            : $"已取消加载，已加入 {nextImportOffset} 张；可继续剩余图片或中止本次加载。";
        UpdateImageLoadingActions();
    }

    private void RetryImageLoading()
    {
        imageLoadingPaused = false;
        if (pendingImageImport is not null)
        {
            ContinueImageImport();
            return;
        }

        loadedThumbnailKeys.Clear();
        failedThumbnailCount = 0;
        imageLoadingLabel.Text = string.Empty;
        foreach (Control control in thumbnailStrip.Controls)
        {
            if (control.Tag is ThumbnailTileState state) state.HasLoadedThumbnail = false;
        }
        RefreshQueueVisuals();
        UpdateImageLoadingActions();
    }

    private void StartRememberedImageRestore()
    {
        StartImageImport(rememberSelectedImages ? rememberedImages : [], true);
    }

    private void StartImageImport(ScannerImageSelection[] images, bool fromMemory)
    {
        StopImageLoading(showMessage: false);
        if (images.Length == 0) return;
        pendingImageImport = images;
        nextImportOffset = 0;
        importIsRemembered = fromMemory;
        ContinueImageImport();
    }

    private void ContinueImageImport()
    {
        if (pendingImageImport is null || rememberedImageRestoreCancellation is not null) return;
        imageLoadingPaused = false;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(imageLoadingLifetime.Token);
        rememberedImageRestoreCancellation = cancellation;
        imageLoadingLabel.Text = $"正在{(importIsRemembered ? "恢复" : "加入")}图片 {nextImportOffset}/{pendingImageImport.Length}（可取消后继续）…";
        UpdateImageLoadingActions();
        _ = ImportImagesAsync(pendingImageImport, cancellation);
    }

    private async Task ImportImagesAsync(ScannerImageSelection[] images, CancellationTokenSource cancellation)
    {
        try
        {
            // Import lightweight path records in batches. Never decode or probe
            // every network image merely to restore a large queue.
            while (nextImportOffset < images.Length)
            {
                await Task.Delay(1, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrentRestore(cancellation)) return;
                int take = Math.Min(32, images.Length - nextImportOffset);
                lock (stateLock)
                {
                    for (int offset = 0; offset < take; offset++)
                    {
                        ScannerImageSelection image = images[nextImportOffset + offset];
                        selectedImages.Add(image);
                    }
                    nextImportOffset += take;
                    revision++;
                }

                RefreshQueueVisuals();
                imageLoadingLabel.Text = $"正在{(importIsRemembered ? "恢复" : "加入")}图片 {nextImportOffset}/{images.Length}（可取消后继续）…";
                UpdateQueueBusyState();
            }

            if (IsCurrentRestore(cancellation))
            {
                pendingImageImport = null;
                if (!importIsRemembered) SaveRememberedImagesFromCurrentSelection();
                imageLoadingLabel.Text = $"已{(importIsRemembered ? "恢复" : "加入")} {images.Length} 张，仅加载当前页缩略图（单张最多 10 秒）。";
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentRestore(cancellation))
            {
                imageLoadingPaused = true;
                imageLoadingLabel.Text = $"加载已暂停，已加入 {nextImportOffset}/{images.Length} 张，可继续。";
            }
        }
        finally
        {
            if (IsCurrentRestore(cancellation))
            {
                rememberedImageRestoreCancellation = null;
                UpdateImageLoadingActions();
                if (pendingImageImport is null && scanAfterImageImport)
                {
                    scanAfterImageImport = false;
                    TriggerScan();
                }
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrentRestore(CancellationTokenSource cancellation)
        => !IsDisposed && !Disposing && ReferenceEquals(rememberedImageRestoreCancellation, cancellation);

    private LoadedThumbnail ReadThumbnail(ScannerImageSelection image, Size size)
    {
        if (!TryCreateThumbnailCacheKey(image, size, out ThumbnailCacheKey key))
        {
            throw new IOException("图片不存在或暂时无法访问。");
        }

        Bitmap bitmap = thumbnailCache.TryClone(key) as Bitmap
            ?? CreateThumbnailForCache(image, size, CancellationToken.None);
        return new LoadedThumbnail(key, bitmap);
    }

    private void CacheLoadedThumbnail(ScannerImageSelection image, LoadedThumbnail loaded)
    {
        loadedThumbnailKeys[image] = loaded.Key;
        thumbnailCache.Store(loaded.Key, new Bitmap(loaded.Bitmap));
    }

    private async Task LoadThumbnailAsync(
        ThumbnailTileState state,
        ScannerImageSelection image,
        Size size,
        int requestId,
        CancellationToken cancellationToken)
    {
        pendingThumbnailLoads++;
        if (imageLoadingLabel.Text.Length == 0) imageLoadingLabel.Text = "正在加载本页预览，可以继续浏览或取消加载。";
        UpdateImageLoadingActions();
        try
        {
            using LoadedThumbnail loaded = await ImageFileOperations.RunAsync(
                () => ReadThumbnail(image, size), cancellationToken);
            if (cancellationToken.IsCancellationRequested || !IsCurrentThumbnail(state, requestId))
            {
                return;
            }

            CacheLoadedThumbnail(image, loaded);
            state.ThumbnailKey = loaded.Key;
            state.HasLoadedThumbnail = true;
            ReplacePictureBoxImage(state.PictureBox, new Bitmap(loaded.Bitmap));
        }
        catch (Exception exception)
        {
            if (!IsCurrentThumbnail(state, requestId))
            {
                return;
            }

            bool canceled = cancellationToken.IsCancellationRequested;
            if (!canceled) failedThumbnailCount++;
            string text = canceled ? "已取消" : exception is TimeoutException ? "加载超时" : "无法读取";
            ReplacePictureBoxImage(state.PictureBox, CreateFallbackThumbnail(size, text + "\n可重试"));
            if (rememberedImageRestoreCancellation is null && !canceled)
            {
                imageLoadingLabel.Text = "部分图片未加载，可重试、删除或重新添加。";
            }

            if (!canceled)
            {
                DiagnosticsLog.WriteException("UI", exception, $"Load thumbnail path={image.Path}");
            }
        }
        finally
        {
            pendingThumbnailLoads--;
            if (IsCurrentThumbnail(state, requestId))
            {
                state.LoadingIndicator.Active = false;
                state.LoadingIndicator.Visible = false;
            }
            if (!IsDisposed && !Disposing)
            {
                UpdateImageLoadingActions();
            }
        }
    }

    private bool IsCurrentThumbnail(ThumbnailTileState state, int requestId)
        => !disposingImageUi && !IsDisposed && !Disposing && !state.PictureBox.IsDisposed && state.ThumbnailRequestId == requestId;

    private sealed class LoadedThumbnail(ThumbnailCacheKey key, Bitmap bitmap) : IDisposable
    {
        internal ThumbnailCacheKey Key { get; } = key;
        internal Bitmap Bitmap { get; } = bitmap;
        public void Dispose() => Bitmap.Dispose();
    }
}

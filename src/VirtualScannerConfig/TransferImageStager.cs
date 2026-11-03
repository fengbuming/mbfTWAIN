using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MbfTwain.VirtualScannerConfig;

// Only the requested image is copied. Keep the current and previous file for
// TWAIN's post-transfer IMAGEINFO query; never stage an entire batch of pixels.
internal sealed class TransferImageStager(Action<uint, int, string> onError) : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<int, PreparedFile> ready = [];
    private CancellationTokenSource? pending;
    private uint revision;
    private bool failed;
    private bool disposed;

    internal void Reset(uint newRevision = 0)
    {
        lock (sync)
        {
            revision = newRevision;
            CancelPendingUnsafe();
            foreach (PreparedFile file in ready.Values) file.Dispose();
            ready.Clear();
            failed = false;
        }
    }

    internal void Pause()
    {
        lock (sync) CancelPendingUnsafe();
    }

    internal void Resume()
    {
        lock (sync) failed = false;
    }

    internal string Request(uint requestedRevision, int index, string source)
    {
        lock (sync)
        {
            if (disposed || revision != requestedRevision) return "ERR stale-session\n";
            if (ready.TryGetValue(index, out PreparedFile? file)) return $"OK IMAGE {file.Path}\n";
            if (failed || pending is not null) return "OK WAIT\n";
            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(ImageFileOperations.ReadTimeout);
            pending = cancellation;
            _ = PrepareAsync(requestedRevision, index, source, cancellation);
            return "OK WAIT\n";
        }
    }

    private async Task PrepareAsync(uint requestedRevision, int index, string source, CancellationTokenSource cancellation)
    {
        try
        {
            PreparedFile result = await ImageFileOperations.RunTransferAsync(
                () => CopySource(source, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed || revision != requestedRevision || !ReferenceEquals(pending, cancellation))
                {
                    result.Dispose();
                    return;
                }

                ready[index] = result;
                while (ready.Count > 2)
                {
                    int oldest = ready.Keys.Min();
                    ready.Remove(oldest, out PreparedFile? previous);
                    previous?.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            bool notify;
            lock (sync)
            {
                notify = !disposed && revision == requestedRevision && ReferenceEquals(pending, cancellation);
                if (notify) failed = true;
            }
            if (notify)
            {
                DiagnosticsLog.WriteException("UI", exception, $"Prepare transfer image index={index}");
                onError(requestedRevision, index, $"第 {index + 1} 张读取失败或超时：{source}\n可重试、跳过此图、更换图片，或中止本次扫描。");
            }
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(pending, cancellation))
                {
                    pending = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private static PreparedFile CopySource(string source, CancellationToken token)
    {
        var result = new PreparedFile(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mbfTwain-transfer-{Guid.NewGuid():N}.img"));
        try
        {
            token.ThrowIfCancellationRequested();
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan))
            {
                token.ThrowIfCancellationRequested();
                using var output = new FileStream(result.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
                byte[] buffer = new byte[65536];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
            }
            token.ThrowIfCancellationRequested();
            using var validation = Image.FromFile(result.Path);
            if (validation.Width <= 0 || validation.Height <= 0) throw new IOException("图片内容无效。");
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private void CancelPendingUnsafe()
    {
        pending?.Cancel();
        pending = null;
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            Reset();
        }
    }

    private sealed class PreparedFile(string path) : IDisposable
    {
        internal string Path { get; } = path;
        public void Dispose()
        {
            try { File.Delete(Path); }
            catch (IOException exception) { DiagnosticsLog.WriteException("UI", exception, "Delete prepared transfer image"); }
            catch (UnauthorizedAccessException exception) { DiagnosticsLog.WriteException("UI", exception, "Delete prepared transfer image"); }
        }
    }
}

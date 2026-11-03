using System;
using System.Threading;
using System.Threading.Tasks;

namespace MbfTwain.VirtualScannerConfig;

internal static class ImageFileOperations
{
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    // Windows file-system calls (including SMB and mapped drives) may not honor
    // cancellation. Bound the outstanding calls as well as the caller's wait;
    // an abandoned call retains its slot until it actually finishes.
    private static readonly SemaphoreSlim ReadSlots = new(2, 2);
    private static readonly SemaphoreSlim TransferSlots = new(1, 1);

    internal static Task<T> RunTransferAsync<T>(Func<T> read, CancellationToken token) where T : IDisposable
        => RunWithSlotsAsync(read, token, ReadTimeout, TransferSlots);

    internal static Task<T> RunAsync<T>(
        Func<T> read,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) where T : IDisposable
        => RunWithSlotsAsync(read, cancellationToken, timeout ?? ReadTimeout, ReadSlots);

    private static async Task<T> RunWithSlotsAsync<T>(
        Func<T> read, CancellationToken cancellationToken, TimeSpan timeout, SemaphoreSlim slots) where T : IDisposable
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await slots.WaitAsync(deadline.Token).ConfigureAwait(false);
            Task<T> work;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                CancellationToken readToken = deadline.Token;
                work = Task.Run(() =>
                {
                    try
                    {
                        readToken.ThrowIfCancellationRequested();
                        return read();
                    }
                    finally
                    {
                        slots.Release();
                    }
                });
            }
            catch
            {
                slots.Release();
                throw;
            }

            try
            {
                return await work.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                // A late bitmap must never reach the UI or leak after timeout,
                // cancellation, queue replacement, or window closure.
                _ = work.ContinueWith(completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        completed.Result.Dispose();
                    }
                    else
                    {
                        _ = completed.Exception;
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取图片超时，请检查网络连接后重试。");
        }
    }
}

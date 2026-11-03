using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using MbfTwain.VirtualScannerConfig;
using MbfTwain.VirtualScannerConfig.Ipc;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo RunRead = typeof(MainForm).Assembly
        .GetType("MbfTwain.VirtualScannerConfig.ImageFileOperations")!
        .GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(Probe));

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Control.CheckForIllegalCrossThreadCalls = true;
        using var messageLoopHost = new Form { ShowInTaskbar = false, Opacity = 0 };
        string directory = Path.Combine(Path.GetTempPath(), "mbfTwain-loading-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int exitCode = 0;
        messageLoopHost.Shown += (_, _) => messageLoopHost.BeginInvoke((Action)(() =>
        {
            try
            {
                VerifyBackgroundReads();
                VerifyRestoration(directory);
                VerifyStalledTransferDeadline(directory);
                VerifyNativePagedTransfer(directory, "x64");
                VerifyNativePagedTransfer(directory, "Win32");
                VerifyNativePagedTransfer(directory, "x64", true);
                VerifyNativePagedTransfer(directory, "Win32", true);
                VerifyNativeIssueRecovery(directory, "x64", false);
                VerifyNativeIssueRecovery(directory, "Win32", false);
                VerifyNativeIssueRecovery(directory, "x64", true);
                VerifyNativeIssueRecovery(directory, "Win32", true);
                Console.WriteLine("ALL IMAGE LOADING SMOKES PASSED");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                exitCode = 1;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
                messageLoopHost.Close();
            }
        }));
        Application.Run(messageLoopHost);
        return exitCode;
    }

    private static Task<Probe> Read(Func<Probe> action, CancellationToken token, int timeoutMilliseconds)
        => (Task<Probe>)RunRead.Invoke(null, [action, token, TimeSpan.FromMilliseconds(timeoutMilliseconds)])!;

    private static void VerifyBackgroundReads()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        using var cancellation = new CancellationTokenSource();
        var first = new Probe();
        var second = new Probe();
        int thirdStarted = 0;
        try
        {
            Task<Probe> timed = Read(() => { entered.Signal(); release.Wait(); return first; }, CancellationToken.None, 250);
            Task<Probe> canceled = Read(() => { entered.Signal(); release.Wait(); return second; }, cancellation.Token, 2000);
            Check(entered.Wait(2000), "two blocked file reads started in background");
            cancellation.Cancel();
            PumpUntil(() => timed.IsCompleted && canceled.IsCompleted, 1500);
            Check(timed.IsFaulted && timed.Exception!.InnerException is TimeoutException, "blocked read times out without waiting for OS call");
            Check(canceled.IsCanceled, "blocked read cancels immediately");
            Task<Probe> queued = Read(() => { Interlocked.Increment(ref thirdStarted); return new Probe(); }, CancellationToken.None, 150);
            PumpUntil(() => queued.IsCompleted, 1000);
            Check(queued.IsFaulted && queued.Exception!.InnerException is TimeoutException && thirdStarted == 0,
                "retries remain bounded while abandoned OS calls occupy both slots");
        }
        finally
        {
            release.Set();
        }

        PumpUntil(() => first.Disposed && second.Disposed, 2000);
        Check(first.DisposeCount == 1 && second.DisposeCount == 1, "late results are disposed exactly once");
        Task<Probe> retry = Read(() => new Probe(), CancellationToken.None, 1000);
        PumpUntil(() => retry.IsCompleted, 1500);
        using Probe result = retry.GetAwaiter().GetResult();
        Check(!result.Disposed, "retry succeeds after blocked calls finish");
    }

    private static MainForm CreateForm(string directory)
    {
        var form = (MainForm)Activator.CreateInstance(typeof(MainForm), PrivateInstance, null, [false], null)!;
        _ = form.Handle;
        Set(form, "uiContext", SynchronizationContext.Current!);
        object store = Get<object>(form, "userSettingsStore");
        store.GetType().GetField("settingsPath", PrivateInstance)!.SetValue(store, Path.Combine(directory, "settings.json"));
        return form;
    }

    private static void VerifyRestoration(string directory)
    {
        string imagePath = Path.Combine(directory, "page.bmp");
        using (var image = new Bitmap(40, 70)) image.Save(imagePath);
        var images = Enumerable.Range(0, 500).Select(index =>
            new ScannerImageSelection(imagePath, (index % 4) * 90)).ToArray();
        images[499] = new ScannerImageSelection(Path.Combine(directory, "missing.bmp"), 0);
        using var form = CreateForm(directory);
        Set(form, "rememberSelectedImages", true);
        Set(form, "rememberedImages", images);
        Invoke(form, "ResetImageSelection");
        Invoke(form, "StartRememberedImageRestore");
        PumpUntil(() => Snapshot(form).SelectedImages.Count >= 32, 2000);
        Invoke(form, "StopImageLoading", true);
        int canceledCount = Snapshot(form).SelectedImages.Count;
        Check(canceledCount > 0 && canceledCount < 500, "500-image restore can cancel between path batches");
        PumpFor(40);
        Check(Snapshot(form).SelectedImages.Count == canceledCount, "cancel preserves import cursor and ignores old work");
        Invoke(form, "RetryImageLoading");
        PumpUntil(() => Get<object?>(form, "rememberedImageRestoreCancellation") is null, 4000);
        Check(Snapshot(form).SelectedImages.SequenceEqual(images), "continue imports all 500 paths in order without duplicates or dropped offline pages");
        var strip = Get<FlowLayoutPanel>(form, "thumbnailStrip");
        Check(strip.Controls.Count == 13, "500-image queue creates only 12 thumbnail tiles and one add tile");
        Invoke(form, "ChangeQueuePage", 41);
        Check(strip.Controls.Count == 9 && Get<int>(form, "queuePageIndex") == 41,
            "last page shows only the remaining eight images");
        object firstState = strip.Controls[0].Tag!;
        Check((int)firstState.GetType().GetProperty("Index")!.GetValue(firstState)! == 492,
            "thumbnail actions retain global image indices after paging");
        Invoke(form, "MoveImage", 492, -1);
        Check(Get<int>(form, "selectedImageIndex") == 491 && Get<int>(form, "queuePageIndex") == 40,
            "reordering across page boundary follows moved image");
        Invoke(form, "ChangeQueuePage", 0);
        PumpUntil(() => Get<int>(form, "pendingThumbnailLoads") == 0, 4000);

        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        using var cancellation = new CancellationTokenSource();
        Task<Probe>[] blocked = Enumerable.Range(0, 2).Select(_ => Read(() =>
        {
            entered.Signal(); release.Wait(); return new Probe();
        }, cancellation.Token, 5000)).ToArray();
        try
        {
            Check(entered.Wait(2000), "two simulated stalled network calls occupy preview reader slots");
            Get<object>(form, "loadedThumbnailKeys").GetType().GetMethod("Clear")!.Invoke(Get<object>(form, "loadedThumbnailKeys"), null);
            Invoke(form, "RetryImageLoading");
            using var heartbeat = new System.Windows.Forms.Timer { Interval = 20 };
            int ticks = 0;
            heartbeat.Tick += (_, _) => ticks++;
            heartbeat.Start();
            PumpUntil(() => ticks >= 5, 1500);
            Check(ticks >= 5, "large queue message loop responds while network thumbnails stall");
            Invoke(form, "StopImageLoading", true);
            Check(Get<Button>(form, "triggerScanButton").Enabled, "cancel restores queue controls immediately");
            using var closePreview = new System.Windows.Forms.Timer { Interval = 100 };
            bool previewClosed = false;
            closePreview.Tick += (_, _) =>
            {
                Form? preview = Application.OpenForms.Cast<Form>().FirstOrDefault(window => window.Text == Path.GetFileName(imagePath));
                if (preview is not null) { preview.Close(); previewClosed = true; closePreview.Stop(); }
            };
            closePreview.Start();
            var watch = Stopwatch.StartNew();
            Invoke(form, "ShowFullscreenPreview", 0);
            Check(previewClosed && watch.ElapsedMilliseconds < 1500, "preview closes immediately while its image read stalls");
            Invoke(form, "ClearImages");
            release.Set(); cancellation.Cancel();
            PumpUntil(() => blocked.All(task => task.IsCompleted), 1500);
            PumpFor(100);
            Check(Snapshot(form).SelectedImages.Count == 0, "late thumbnail completions cannot repopulate a cleared queue");
        }
        finally
        {
            cancellation.Cancel(); release.Set();
            foreach (Task<Probe> task in blocked)
            {
                PumpUntil(() => task.IsCompleted, 1500);
                if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception;
            }
        }

        Set(form, "rememberedImages", images);
        Invoke(form, "StartRememberedImageRestore");
        PumpUntil(() => Get<object?>(form, "rememberedImageRestoreCancellation") is null, 4000);
        Set(form, "transferBufferDelayMilliseconds", 0);
        Invoke(form, "TriggerScan");
        uint scanRevision = Get<uint>(form, "activeScanRevision");
        ScannerStateSnapshot state = (ScannerStateSnapshot)Invoke(form, "GetPageSnapshot", 0, 0)!;
        Check(state.SelectedImages.Count == 0 && state.TotalImageCount == 500 && state.PagedImages,
            "scan polling contains totals and flags without resending all 500 paths");
        ScannerStateSnapshot page = (ScannerStateSnapshot)Invoke(form, "GetPageSnapshot", 480, 32)!;
        Check(page.SelectedImages.Count == 20 && page.ImageOffset == 480, "transfer metadata is served in bounded pages");
        string reply = (string)Invoke(form, "GetTransferImageResponse", scanRevision, 0)!;
        PumpUntil(() => ((string)Invoke(form, "GetTransferImageResponse", scanRevision, 0)!).StartsWith("OK IMAGE "), 3000);
        Invoke(form, "PauseTransfer", (object?)null);
        Check(((string)Invoke(form, "GetTransferImageResponse", scanRevision, 1)!) == "OK WAIT\n",
            "cancel pauses further image preparation without changing scan revision");
        Invoke(form, "ResumeTransfer");
        Check(Snapshot(form).ScanRequested && !Snapshot(form).TransferPaused && Get<uint>(form, "activeScanRevision") == scanRevision,
            "continue preserves the active scan identity");
        Invoke(form, "GetTransferImageResponse", scanRevision, 499);
        PumpUntil(() => Snapshot(form).TransferPaused, 3000);
        Check(Get<object?>(form, "transferProgressDialog") is not null,
            "unreadable transfer image pauses delivery with continue and abort controls");
        File.Copy(imagePath, images[499].Path);
        Invoke(form, "ResumeTransfer");
        PumpUntil(() => ((string)Invoke(form, "GetTransferImageResponse", scanRevision, 499)!).StartsWith("OK IMAGE "), 3000);
        Check(!Snapshot(form).TransferPaused, "continue retries the failed page after the source becomes available");
        Invoke(form, "ApplyTransferProgress", scanRevision, 10, 500);
        Invoke(form, "CancelTransferProgressRequest");
        Check(!Snapshot(form).ScanRequested && Snapshot(form).SelectedImages.SequenceEqual(images.Skip(10)),
            "abort keeps only untransferred pages in the working queue");
        Invoke(form, "ApplyTransferProgress", scanRevision, 11, 500);
        Invoke(form, "AcknowledgeScan", scanRevision);
        Check(Snapshot(form).SelectedImages.Count == 490, "stale progress and ACK cannot clear the remaining queue");
        Check(((string)Invoke(form, "GetTransferImageResponse", scanRevision, 0)!).StartsWith("ERR"),
            "aborted scan cannot prepare more files");
        Invoke(form, "ClearImages");
        Invoke(form, "StartImageImport", images, false);
        PumpUntil(() => Snapshot(form).SelectedImages.Count >= 32, 2000);
        Invoke(form, "StopImageLoading", false);
        int stoppedAt = Snapshot(form).SelectedImages.Count;
        PumpFor(100);
        Check(Snapshot(form).SelectedImages.Count == stoppedAt && Get<object?>(form, "pendingImageImport") is null,
            "abort import discards pending work and retains already imported images");
    }

    private static void VerifyStalledTransferDeadline(string directory)
    {
        string pipeName = "mbfTwain-stalled-image-" + Guid.NewGuid().ToString("N");
        using var stalledSource = new System.IO.Pipes.NamedPipeServerStream(pipeName,
            System.IO.Pipes.PipeDirection.Out, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);
        Task connected = stalledSource.WaitForConnectionAsync();
        using var form = CreateForm(directory);
        Get<List<ScannerImageSelection>>(form, "selectedImages").Add(new ScannerImageSelection(@"\\.\pipe\" + pipeName, 0));
        Invoke(form, "TriggerScan");
        uint scanRevision = Get<uint>(form, "activeScanRevision");
        var watch = Stopwatch.StartNew();
        Invoke(form, "GetTransferImageResponse", scanRevision, 0);
        try
        {
            PumpUntil(() => connected.IsCompleted, 3000);
            PumpUntil(() => Snapshot(form).TransferPaused, 13000);
            Check(watch.ElapsedMilliseconds >= 9000 && watch.ElapsedMilliseconds < 13000,
                "a non-returning Windows file read pauses delivery at the ten-second deadline");
            Check(Get<int>(form, "failedTransferImageIndex") == 0,
                "timeout offers per-image retry, skip, replace and abort choices");
        }
        finally
        {
            stalledSource.Dispose();
            Invoke(form, "CancelTransferProgressRequest");
            PumpFor(100);
        }
    }

    private static void VerifyNativePagedTransfer(string directory, string platform, bool memory = false)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string nativeDirectory = Path.Combine(root, "build", "manual", platform, "Release");
        string imagePath = Path.Combine(directory, memory ? "memory.bmp" : "page.bmp");
        if (memory) { using var image = new Bitmap(2, 2); image.Save(imagePath); }
        var images = Enumerable.Range(0, 500).Select(index => new ScannerImageSelection(imagePath, index % 2 == 0 ? 0 : 90)).ToArray();
        using var form = CreateForm(directory);
        Set(form, "rememberSelectedImages", true);
        Set(form, "rememberedImages", images);
        Set(form, "transferBufferDelayMilliseconds", 0);
        Invoke(form, "StartPipeServer");
        var start = new ProcessStartInfo(Path.Combine(nativeDirectory, "SmokeDsEntry.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(nativeDirectory, "mbfVirtualTwainDS.ds"));
        start.Environment["MBF_TWAIN_FORCE_UI"] = "1";
        start.Environment["MBF_SMOKE_EXPECT_XFERREADY"] = "1";
        start.Environment["MBF_SMOKE_EXPECT_ENABLE_CALLBACK"] = "1";
        start.Environment["MBF_SMOKE_EXPECT_TRANSFER_TOTAL"] = "500";
        foreach (string key in new[] { "MBF_SMOKE_SET_XFERCOUNT", "MBF_SMOKE_USE_MEMORY", "MBF_SMOKE_EXPECT_CLOSEDSREQ" }) start.Environment.Remove(key);
        if (memory)
        {
            start.Environment["MBF_SMOKE_SET_XFERCOUNT"] = "1";
            start.Environment["MBF_SMOKE_USE_MEMORY"] = "1";
            start.Environment["MBF_SMOKE_EXPECT_TRANSFER_TOTAL"] = "1";
        }
        using Process host = Process.Start(start)!;
        Task<string> output = host.StandardOutput.ReadToEndAsync();
        Task<string> error = host.StandardError.ReadToEndAsync();
        bool triggered = false;
        bool paused = false;
        bool resumed = false;
        int pausedAt = 0;
        var pauseWatch = new Stopwatch();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            if (!triggered && form.Visible && Get<object?>(form, "rememberedImageRestoreCancellation") is null && Snapshot(form).SelectedImages.Count == 500)
            {
                triggered = true;
                Invoke(form, "TriggerScan");
            }
            int completed = Get<int>(form, "transferCompletedImages");
            if (!memory && !paused && completed >= 5 && Snapshot(form).ScanRequested)
            {
                paused = true; pausedAt = completed; Invoke(form, "PauseTransfer", (object?)null); pauseWatch.Start();
            }
            if (paused && !resumed && pauseWatch.ElapsedMilliseconds >= 250)
            {
                Check(completed <= pausedAt + 1, platform + " native sender holds its page cursor while canceled");
                Invoke(form, "ResumeTransfer"); resumed = true;
            }
        };
        timer.Start();
        try
        {
            PumpUntil(() => host.HasExited, 120000);
            PumpUntil(() => output.IsCompleted && error.IsCompleted, 3000);
            string logPath = Path.Combine(root, "build", $"verify-bulk-{platform}{(memory ? "-memory" : "")}.log");
            File.WriteAllText(logPath, output.Result + error.Result);
            Check(host.ExitCode == 0 && triggered && (memory || (paused && resumed)),
                platform + (memory ? " paged memory transfer obeys CAP_XFERCOUNT=1 from a 500-image queue"
                    : " native TWAIN transfers all 500 pages across metadata batches after cancel/continue"));
            if (!memory) Check(output.Result.Contains("MSG_GET #500: XFERDONE"), platform + " host confirms final page and per-page transfer sequence");
        }
        finally
        {
            timer.Stop();
            if (!host.HasExited) { Invoke(form, "CancelTransferProgressRequest"); PumpFor(100); host.Kill(); host.WaitForExit(); }
            Invoke(form, "CancelPendingExit");
        }
    }

    private static void VerifyNativeIssueRecovery(string directory, string platform, bool allMissing)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string nativeDirectory = Path.Combine(root, "build", "manual", platform, "Release");
        string validPath = Path.Combine(directory, "page.bmp");
        string missingPath = Path.Combine(directory, "unavailable-" + platform + ".bmp");
        string corruptPath = Path.Combine(directory, "corrupt.bmp");
        File.WriteAllText(corruptPath, "This file is not an image");
        int count = allMissing ? 3 : 40;
        var images = Enumerable.Range(0, count).Select(index => new ScannerImageSelection(
            allMissing || index is 0 or 17 or 31 or 39 ? missingPath : index == 16 ? corruptPath : validPath, 0)).ToArray();
        int expected = allMissing ? 0 : 36;
        using var form = CreateForm(directory);
        Set(form, "rememberSelectedImages", true);
        Set(form, "rememberedImages", images);
        Set(form, "transferBufferDelayMilliseconds", 0);
        Invoke(form, "StartPipeServer");
        var start = new ProcessStartInfo(Path.Combine(nativeDirectory, "SmokePagedTransfer.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(nativeDirectory, "mbfVirtualTwainDS.ds"));
        start.ArgumentList.Add(expected.ToString());
        start.Environment["MBF_TWAIN_FORCE_UI"] = "1";
        using Process host = Process.Start(start)!;
        Task<string> output = host.StandardOutput.ReadToEndAsync();
        Task<string> error = host.StandardError.ReadToEndAsync();
        bool triggered = false;
        int decisions = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            if (!triggered && form.Visible && Get<object?>(form, "rememberedImageRestoreCancellation") is null && Snapshot(form).SelectedImages.Count == count)
            {
                triggered = true;
                Invoke(form, "TriggerScan");
            }
            if (Snapshot(form).TransferPaused && Get<int>(form, "failedTransferImageIndex") is int failed && failed >= 0)
            {
                decisions++;
                if (!allMissing && failed == 17) Invoke(form, "ReplaceFailedTransferImageWith", validPath);
                else Invoke(form, "SkipFailedTransferImage");
            }
        };
        timer.Start();
        try
        {
            PumpUntil(() => host.HasExited, 30000);
            PumpUntil(() => output.IsCompleted && error.IsCompleted, 2000);
            File.WriteAllText(Path.Combine(root, "build", $"verify-issues-{platform}-{allMissing}.log"), output.Result + error.Result);
            Check(host.ExitCode == 0 && triggered && decisions == (allMissing ? 3 : 5),
                platform + (allMissing ? " all missing images can be skipped and the scan closes gracefully"
                    : " missing/corrupt pages can be skipped or replaced without failing the batch, including a batch boundary and last page"));
            PumpUntil(() => Get<object?>(form, "transferSummaryDialog") is not null, 1500);
            Check(Get<HashSet<int>>(form, "skippedTransferIndices").Count == (allMissing ? 3 : 4),
                "completion summary retains every skipped page for user review");
        }
        finally
        {
            timer.Stop();
            if (!host.HasExited) { Invoke(form, "CancelTransferProgressRequest"); PumpFor(100); host.Kill(); host.WaitForExit(); }
            Invoke(form, "CancelPendingExit");
            Set(form, "exitAfterTransferSummary", false);
            Get<Form?>(form, "transferSummaryDialog")?.Close();
        }
    }

    private static T Get<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value)
        => instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object?[] arguments)
        => instance.GetType().GetMethod(name, PrivateInstance)!.Invoke(instance, arguments);
    private static ScannerStateSnapshot Snapshot(MainForm form) => (ScannerStateSnapshot)Invoke(form, "GetSnapshot")!;

    private static void PumpUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }

        Check(condition(), "asynchronous operation completed within the acceptance deadline");
    }

    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed class Probe : IDisposable
    {
        private int disposeCount;
        public int DisposeCount => Volatile.Read(ref disposeCount);
        public bool Disposed => DisposeCount != 0;
        public void Dispose() => Interlocked.Increment(ref disposeCount);
    }
}

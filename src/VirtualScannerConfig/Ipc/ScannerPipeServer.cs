using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MbfTwain.VirtualScannerConfig.Ipc;

public sealed class ScannerPipeServer : IDisposable
{
    public const string PipeName = "mbfTwain.VirtualScanner.v1";
    private static readonly TimeSpan IdleTimeout = GetIdleTimeout();

    private readonly Func<ScannerStateSnapshot> getSnapshot;
    private readonly Func<int, int, ScannerStateSnapshot>? getPageSnapshot;
    private readonly Func<uint, int, string>? getTransferImage;
    private readonly Action<ScannerSessionSettings?> beginScan;
    private readonly Action<uint> hideScanUi;
    private readonly Action<uint> acknowledgeScan;
    private readonly Action<uint, int, int> reportTransferProgress;
    private readonly Action? onPipeNameInUse;
    private readonly Action? onIdleTimeout;
    private readonly CancellationTokenSource cancellation = new();
    private System.Threading.Timer? idleTimer;
    private Task? serverTask;
    private long lastActivityTicks;
    private int idleExitSignaled;

    public ScannerPipeServer(
        Func<ScannerStateSnapshot> getSnapshot,
        Action<ScannerSessionSettings?> beginScan,
        Action<uint> hideScanUi,
        Action<uint> acknowledgeScan,
        Action<uint, int, int> reportTransferProgress,
        Action? onPipeNameInUse = null,
        Action? onIdleTimeout = null,
        Func<int, int, ScannerStateSnapshot>? getPageSnapshot = null,
        Func<uint, int, string>? getTransferImage = null)
    {
        this.getSnapshot = getSnapshot;
        this.getPageSnapshot = getPageSnapshot;
        this.getTransferImage = getTransferImage;
        this.beginScan = beginScan;
        this.hideScanUi = hideScanUi;
        this.acknowledgeScan = acknowledgeScan;
        this.reportTransferProgress = reportTransferProgress;
        this.onPipeNameInUse = onPipeNameInUse;
        this.onIdleTimeout = onIdleTimeout;
    }

    public void Start()
    {
        DiagnosticsLog.Write("UI-IPC", $"Starting pipe server {PipeName}");
        if (serverTask is null)
        {
            lastActivityTicks = DateTime.UtcNow.Ticks;
            serverTask = Task.Run(() => RunAsync(cancellation.Token));
            idleTimer = new System.Threading.Timer(CheckIdleTimeout, null, IdleTimeout, IdleTimeout);
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        idleTimer?.Dispose();
        try
        {
            serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipeInstance();
                DiagnosticsLog.Write("UI-IPC", $"Waiting for connection on {PipeName}");
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                DiagnosticsLog.Write("UI-IPC", "Client connected");
                MarkActivity();
                await HandleClientAsync(pipe, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                DiagnosticsLog.Write("UI-IPC", "Pipe server cancellation requested");
                break;
            }
            catch (UnauthorizedAccessException)
            {
                DiagnosticsLog.Write("UI-IPC", $"Pipe name {PipeName} is already in use by another UI instance; exiting");
                NotifyPipeNameInUse();
                break;
            }
            catch (IOException exception) when (IsAccessDenied(exception))
            {
                DiagnosticsLog.WriteException("UI-IPC", exception, "RunAsync pipe name in use");
                NotifyPipeNameInUse();
                break;
            }
            catch (IOException exception)
            {
                DiagnosticsLog.WriteException("UI-IPC", exception, "RunAsync");
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private void CheckIdleTimeout(object? state)
    {
        try
        {
            long last = Interlocked.Read(ref lastActivityTicks);
            if (DateTime.UtcNow.Ticks - last > IdleTimeout.Ticks &&
                Interlocked.CompareExchange(ref idleExitSignaled, 1, 0) == 0)
            {
                DiagnosticsLog.Write(
                    "UI-IPC",
                    $"Pipe server idle for {IdleTimeout.TotalMinutes:0.#} minutes; exiting");
                NotifyIdleTimeout();
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.WriteException("UI-IPC", exception, "CheckIdleTimeout");
        }
    }

    private void MarkActivity()
    {
        Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
    }

    private static NamedPipeServerStream CreatePipeInstance()
    {
        var pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        // Allow clients of the same user across UAC integrity levels to connect:
        // grant Everyone/SYSTEM/Administrators read+write and set a Medium
        // mandatory label so a non-elevated client can also write to the pipe.
        ApplyPermissiveSecurity(pipe.SafePipeHandle);
        return pipe;
    }

    private static void ApplyPermissiveSecurity(Microsoft.Win32.SafeHandles.SafePipeHandle handle)
    {
        const string sddl = "D:P(A;;GA;;;WD)(A;;GA;;;SY)(A;;GA;;;BA)(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                1,
                out IntPtr securityDescriptor,
                out _))
        {
            DiagnosticsLog.Write("UI-IPC", "ApplyPermissiveSecurity failed to convert SDDL");
            return;
        }

        try
        {
            if (!GetSecurityDescriptorDacl(
                    securityDescriptor,
                    out bool daclPresent,
                    out IntPtr dacl,
                    out _))
            {
                DiagnosticsLog.Write("UI-IPC", "ApplyPermissiveSecurity failed to read DACL");
                return;
            }

            if (!GetSecurityDescriptorSacl(
                    securityDescriptor,
                    out bool saclPresent,
                    out IntPtr sacl,
                    out _))
            {
                DiagnosticsLog.Write("UI-IPC", "ApplyPermissiveSecurity failed to read SACL");
                return;
            }

            const uint securityInformation =
                (uint)SecurityInformationFlags.Dacl | (uint)SecurityInformationFlags.Label;
            uint result = SetSecurityInfo(
                handle,
                SeObjectType.KernelObject,
                securityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                daclPresent ? dacl : IntPtr.Zero,
                saclPresent ? sacl : IntPtr.Zero);
            if (result != 0)
            {
                DiagnosticsLog.Write(
                    "UI-IPC",
                    "ApplyPermissiveSecurity SetSecurityInfo failed error=" + result);
            }
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl,
        uint revision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        out bool daclPresent,
        out IntPtr dacl,
        out bool daclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor,
        out bool saclPresent,
        out IntPtr sacl,
        out bool saclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        System.Runtime.InteropServices.SafeHandle handle,
        SeObjectType objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private enum SeObjectType : uint
    {
        KernelObject = 6,
    }

    [Flags]
    private enum SecurityInformationFlags : uint
    {
        Dacl = 0x4,
        Label = 0x10,
    }

    private static bool IsAccessDenied(IOException exception)
    {
        return exception.HResult == unchecked((int)0x80070005);
    }

    private static TimeSpan GetIdleTimeout()
    {
        string? raw = Environment.GetEnvironmentVariable("MBF_TWAIN_UI_IDLE_TIMEOUT_MINUTES");
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return TimeSpan.FromMinutes(10);
    }

    private void NotifyPipeNameInUse()
    {
        try
        {
            onPipeNameInUse?.Invoke();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.WriteException("UI-IPC", exception, "onPipeNameInUse callback");
        }
    }

    private void NotifyIdleTimeout()
    {
        try
        {
            onIdleTimeout?.Invoke();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.WriteException("UI-IPC", exception, "onIdleTimeout callback");
        }
    }

    private async Task HandleClientAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        string? command = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(command))
        {
            DiagnosticsLog.Write("UI-IPC", "Received empty command");
            await writer.WriteLineAsync("ERR empty-command").ConfigureAwait(false);
            return;
        }

        DiagnosticsLog.Write("UI-IPC", $"Received command: {command}");

        if (string.Equals(command, "PING", StringComparison.Ordinal))
        {
            await writer.WriteAsync("OK PONG\n").ConfigureAwait(false);
            DiagnosticsLog.Write("UI-IPC", "Responded with OK PONG");
            return;
        }

        if (string.Equals(command, "GET_STATE", StringComparison.Ordinal))
        {
            ScannerStateSnapshot snapshot = getPageSnapshot?.Invoke(0, 0) ?? getSnapshot();
            DiagnosticsLog.Write(
                "UI-IPC",
                $"GET_STATE revision={snapshot.Revision} scan={snapshot.ScanRequested} images={snapshot.SelectedImages.Count} pixel={snapshot.PixelType} paper={snapshot.PaperSize} xres={snapshot.XResolution} yres={snapshot.YResolution}");
            await WriteStateAsync(writer, snapshot).ConfigureAwait(false);
            return;
        }

        if (command.StartsWith("GET_IMAGE_PAGE ", StringComparison.Ordinal))
        {
            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (getPageSnapshot is null || parts.Length != 4 ||
                !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint requestedRevision) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int offset) ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
                offset < 0 || count < 1 || count > 32)
            {
                await writer.WriteAsync("ERR invalid-page\n").ConfigureAwait(false);
                return;
            }
            ScannerStateSnapshot page = getPageSnapshot(offset, count);
            if (!page.ScanRequested || page.Revision != requestedRevision || offset >= page.TotalImageCount)
            {
                await writer.WriteAsync("ERR stale-session\n").ConfigureAwait(false);
                return;
            }
            await WriteStateAsync(writer, page).ConfigureAwait(false);
            return;
        }

        if (command.StartsWith("GET_TRANSFER_IMAGE ", StringComparison.Ordinal))
        {
            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string reply = parts.Length == 3 && getTransferImage is not null &&
                uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint imageRevision) &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int imageIndex) && imageIndex >= 0
                ? getTransferImage(imageRevision, imageIndex) : "ERR invalid-image\n";
            await writer.WriteAsync(reply).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command, "BEGIN_SCAN", StringComparison.Ordinal) ||
            command.StartsWith("BEGIN_SCAN ", StringComparison.Ordinal))
        {
            DiagnosticsLog.Write("UI-IPC", "BEGIN_SCAN accepted");
            beginScan(ParseBeginScanSettings(command));
            await writer.WriteAsync("OK BEGIN_SCAN\n").ConfigureAwait(false);
            return;
        }

        const string ackPrefix = "ACK_SCAN ";
        const string hidePrefix = "HIDE_SCAN_UI ";
        const string progressPrefix = "TRANSFER_PROGRESS ";
        if (command.StartsWith(hidePrefix, StringComparison.Ordinal) &&
            uint.TryParse(command.AsSpan(hidePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out uint hideRevision))
        {
            DiagnosticsLog.Write("UI-IPC", $"HIDE_SCAN_UI accepted revision={hideRevision}");
            hideScanUi(hideRevision);
            await writer.WriteAsync("OK HIDE\n").ConfigureAwait(false);
            return;
        }

        if (command.StartsWith(progressPrefix, StringComparison.Ordinal) &&
            TryParseTransferProgress(command.AsSpan(progressPrefix.Length), out uint progressRevision, out int completedImages, out int totalImages))
        {
            DiagnosticsLog.Write(
                "UI-IPC",
                $"TRANSFER_PROGRESS accepted revision={progressRevision} completed={completedImages} total={totalImages}");
            reportTransferProgress(progressRevision, completedImages, totalImages);
            await writer.WriteAsync("OK PROGRESS\n").ConfigureAwait(false);
            return;
        }

        if (command.StartsWith(ackPrefix, StringComparison.Ordinal) &&
            uint.TryParse(command.AsSpan(ackPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out uint revision))
        {
            DiagnosticsLog.Write("UI-IPC", $"ACK_SCAN accepted revision={revision}");
            acknowledgeScan(revision);
            await writer.WriteAsync("OK ACK\n").ConfigureAwait(false);
            return;
        }

        DiagnosticsLog.Write("UI-IPC", $"Unknown command: {command}");
        await writer.WriteLineAsync("ERR unknown-command").ConfigureAwait(false);
    }

    private static async Task WriteStateAsync(StreamWriter writer, ScannerStateSnapshot snapshot)
    {
        await writer.WriteLineAsync("OK STATE").ConfigureAwait(false);
        await writer.WriteLineAsync(FormattableString.Invariant($"revision {snapshot.Revision}")).ConfigureAwait(false);
        await writer.WriteLineAsync(FormattableString.Invariant($"duplex {(snapshot.DuplexEnabled ? 1 : 0)}")).ConfigureAwait(false);
        await writer.WriteLineAsync($"pixel {snapshot.PixelType}").ConfigureAwait(false);
        await writer.WriteLineAsync($"paper {snapshot.PaperSize}").ConfigureAwait(false);
        await writer.WriteLineAsync(FormattableString.Invariant($"xres {snapshot.XResolution}")).ConfigureAwait(false);
        await writer.WriteLineAsync(FormattableString.Invariant($"yres {snapshot.YResolution}")).ConfigureAwait(false);
        await writer.WriteLineAsync(FormattableString.Invariant($"transferDelayMs {snapshot.TransferBufferDelayMilliseconds}")).ConfigureAwait(false);
        await writer.WriteLineAsync(FormattableString.Invariant($"scan {(snapshot.ScanRequested ? 1 : 0)}")).ConfigureAwait(false);
        if (snapshot.PagedImages)
        {
            await writer.WriteLineAsync($"paged 1").ConfigureAwait(false);
            await writer.WriteLineAsync(FormattableString.Invariant($"totalImages {snapshot.TotalImageCount}")).ConfigureAwait(false);
            await writer.WriteLineAsync(FormattableString.Invariant($"offset {snapshot.ImageOffset}")).ConfigureAwait(false);
            await writer.WriteLineAsync($"paused {(snapshot.TransferPaused ? 1 : 0)}").ConfigureAwait(false);
        }
        foreach (ScannerImageSelection image in snapshot.SelectedImages)
        {
            await writer
                .WriteLineAsync(FormattableString.Invariant($"image {image.RotationDegrees}|{image.Path}"))
                .ConfigureAwait(false);
        }

        await writer.WriteLineAsync("END").ConfigureAwait(false);
    }

    private static ScannerSessionSettings? ParseBeginScanSettings(string command)
    {
        string[] tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1)
        {
            return null;
        }

        bool duplexEnabled = false;
        string pixelType = "RGB";
        string paperSize = "A4";
        int xResolution = 300;
        int yResolution = 300;
        bool sawSetting = false;

        foreach (string token in tokens.Skip(1))
        {
            string[] parts = token.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "duplex":
                    if (parts[1] is "0" or "1")
                    {
                        duplexEnabled = parts[1] == "1";
                        sawSetting = true;
                    }
                    break;
                case "pixel":
                    if (parts[1] is "BW" or "GRAY" or "RGB")
                    {
                        pixelType = parts[1];
                        sawSetting = true;
                    }
                    break;
                case "paper":
                    if (parts[1] is "A4" or "A3")
                    {
                        paperSize = parts[1];
                        sawSetting = true;
                    }
                    break;
                case "xres":
                    if (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedXResolution))
                    {
                        xResolution = parsedXResolution;
                        sawSetting = true;
                    }
                    break;
                case "yres":
                    if (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedYResolution))
                    {
                        yResolution = parsedYResolution;
                        sawSetting = true;
                    }
                    break;
            }
        }

        return sawSetting
            ? new ScannerSessionSettings(duplexEnabled, pixelType, paperSize, xResolution, yResolution)
            : null;
    }

    private static bool TryParseTransferProgress(
        ReadOnlySpan<char> payload,
        out uint revision,
        out int completedImages,
        out int totalImages)
    {
        revision = 0;
        completedImages = 0;
        totalImages = 0;

        string[] parts = payload.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out revision) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out completedImages) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out totalImages))
        {
            return false;
        }

        return completedImages >= 0 && totalImages > 0;
    }
}

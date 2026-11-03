using System.Collections.Generic;

namespace MbfTwain.VirtualScannerConfig.Ipc;

public sealed record ScannerImageSelection(
    string Path,
    int RotationDegrees);

public sealed record ScannerStateSnapshot(
    uint Revision,
    bool DuplexEnabled,
    string PixelType,
    string PaperSize,
    int XResolution,
    int YResolution,
    int TransferBufferDelayMilliseconds,
    bool ScanRequested,
    IReadOnlyList<ScannerImageSelection> SelectedImages,
    int TotalImageCount = -1,
    int ImageOffset = 0,
    bool TransferPaused = false,
    bool PagedImages = false);

public sealed record ScannerSessionSettings(
    bool DuplexEnabled,
    string PixelType,
    string PaperSize,
    int XResolution,
    int YResolution);

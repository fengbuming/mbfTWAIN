#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include <cstdint>
#include <string>
#include <vector>

namespace mbf::twain
{

struct ScannerIpcImage
{
    std::wstring path;
    std::uint32_t rotationDegrees = 0;
};

struct ScannerIpcState
{
    std::uint32_t revision = 0;
    bool duplexEnabled = false;
    std::wstring pixelType = L"RGB";
    std::wstring paperSize = L"A4";
    std::uint32_t xResolution = 300;
    std::uint32_t yResolution = 300;
    std::uint32_t transferBufferDelayMilliseconds = 100;
    bool scanRequested = false;
    bool pagedImages = false;
    bool transferPaused = false;
    std::uint32_t totalImages = 0;
    std::uint32_t imageOffset = 0;
    std::vector<ScannerIpcImage> selectedImages;
};

class ScannerIpcClient final
{
public:
    explicit ScannerIpcClient(std::wstring pipeName = L"mbfTwain.VirtualScanner.v1");

    bool Ping(DWORD timeoutMilliseconds = 150) const;
    bool BeginScan(DWORD timeoutMilliseconds = 150, DWORD* lastError = nullptr) const;
    bool BeginScan(const ScannerIpcState& initialState, DWORD timeoutMilliseconds = 150, DWORD* lastError = nullptr) const;
    bool TryGetState(ScannerIpcState& state, DWORD timeoutMilliseconds = 150) const;
    bool TryGetImagePage(std::uint32_t revision, std::uint32_t offset, std::uint32_t count,
        ScannerIpcState& state, DWORD timeoutMilliseconds = 150) const;
    bool TryPrepareImage(std::uint32_t revision, std::uint32_t index, std::wstring& path, bool& skip,
        DWORD timeoutMilliseconds = 150) const;
    bool ReportTransferProgress(
        std::uint32_t revision,
        std::uint32_t completedImages,
        std::uint32_t totalImages,
        DWORD timeoutMilliseconds = 150) const;
    bool HideScanUi(std::uint32_t revision, DWORD timeoutMilliseconds = 150) const;
    bool AcknowledgeScan(std::uint32_t revision, DWORD timeoutMilliseconds = 150) const;

private:
    bool SendCommand(
        const std::string& command,
        std::string& response,
        DWORD timeoutMilliseconds,
        DWORD* lastError = nullptr) const;

    std::wstring pipePath_;
};

} // namespace mbf::twain

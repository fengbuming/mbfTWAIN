#include "ScannerIpcClient.h"
#include "DiagnosticsLog.h"

#include <algorithm>
#include <array>
#include <cerrno>
#include <cstdlib>
#include <sstream>
#include <string_view>
#include <utility>

namespace
{

std::wstring Utf8ToWide(const std::string& value);
std::string WideToUtf8(const std::wstring& value);

std::wstring CommandName(const std::string& command)
{
    const size_t end = command.find('\n');
    return Utf8ToWide(command.substr(0, end));
}

std::wstring ResponseSnippet(const std::string& response)
{
    constexpr size_t kMaxResponseBytes = 160;
    const std::string_view view(response.data(), (std::min)(response.size(), kMaxResponseBytes));
    return Utf8ToWide(std::string(view));
}

std::wstring Utf8ToWide(const std::string& value)
{
    if (value.empty())
    {
        return {};
    }

    const int length = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0);
    if (length <= 0)
    {
        return {};
    }

    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        result.data(),
        length);
    return result;
}

std::string WideToUtf8(const std::wstring& value)
{
    if (value.empty())
    {
        return {};
    }

    const int length = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (length <= 0)
    {
        return {};
    }

    std::string result(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        result.data(),
        length,
        nullptr,
        nullptr);
    return result;
}

bool TransferPipeBytes(HANDLE pipe, bool writing, void* buffer, DWORD length,
    DWORD& transferred, ULONGLONG deadline, DWORD& error)
{
    OVERLAPPED operation{};
    operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (operation.hEvent == nullptr) { error = GetLastError(); return false; }
    const BOOL started = writing
        ? WriteFile(pipe, buffer, length, &transferred, &operation)
        : ReadFile(pipe, buffer, length, &transferred, &operation);
    bool success = started != FALSE;
    error = success ? ERROR_SUCCESS : GetLastError();
    if (!success && error == ERROR_IO_PENDING)
    {
        const ULONGLONG now = GetTickCount64();
        const DWORD remaining = now >= deadline ? 0U : static_cast<DWORD>((std::min)(deadline - now, 0xfffffffeULL));
        if (WaitForSingleObject(operation.hEvent, remaining) == WAIT_OBJECT_0)
        {
            success = GetOverlappedResult(pipe, &operation, &transferred, FALSE) != FALSE;
            error = success ? ERROR_SUCCESS : GetLastError();
        }
        else
        {
            CancelIoEx(pipe, &operation);
            WaitForSingleObject(operation.hEvent, INFINITE);
            error = ERROR_TIMEOUT;
        }
    }
    CloseHandle(operation.hEvent);
    return success;
}

bool ParseUInt32(std::string_view text, std::uint32_t& value) noexcept
{
    std::string owned(text);
    char* end = nullptr;
    errno = 0;
    const unsigned long parsed = std::strtoul(owned.c_str(), &end, 10);
    if (errno != 0 || end == owned.c_str() || end == nullptr || *end != '\0')
    {
        return false;
    }

    value = static_cast<std::uint32_t>(parsed);
    return static_cast<unsigned long>(value) == parsed;
}

bool ParseBool(std::string_view text, bool& value) noexcept
{
    if (text == "1")
    {
        value = true;
        return true;
    }
    if (text == "0")
    {
        value = false;
        return true;
    }

    return false;
}

bool StartsWith(std::string_view text, std::string_view prefix) noexcept
{
    return text.size() >= prefix.size() &&
           text.substr(0, prefix.size()) == prefix;
}

bool ParseImagePayload(std::string_view text, mbf::twain::ScannerIpcImage& image)
{
    const size_t separator = text.find('|');
    if (separator != std::string_view::npos)
    {
        std::uint32_t rotationDegrees = 0;
        const std::string_view rotationText = text.substr(0, separator);
        const std::string_view pathText = text.substr(separator + 1U);
        if (ParseUInt32(rotationText, rotationDegrees))
        {
            image.rotationDegrees = rotationDegrees % 360U;
            image.path = Utf8ToWide(std::string(pathText));
            return !image.path.empty();
        }
    }

    image.rotationDegrees = 0;
    image.path = Utf8ToWide(std::string(text));
    return !image.path.empty();
}

bool ParseStateResponse(const std::string& response, mbf::twain::ScannerIpcState& state)
{
    std::istringstream stream(response);
    std::string line;

    if (!std::getline(stream, line))
    {
        return false;
    }
    if (!line.empty() && line.back() == '\r')
    {
        line.pop_back();
    }
    if (line != "OK STATE")
    {
        return false;
    }

    mbf::twain::ScannerIpcState parsed{};
    bool sawEnd = false;
    while (std::getline(stream, line))
    {
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }

        if (line == "END")
        {
            sawEnd = true;
            break;
        }

        const std::string_view kRevision("revision ", 9);
        const std::string_view kDuplex("duplex ", 7);
        const std::string_view kPixel("pixel ", 6);
        const std::string_view kPaper("paper ", 6);
        const std::string_view kXRes("xres ", 5);
        const std::string_view kYRes("yres ", 5);
        const std::string_view kTransferDelayMs("transferDelayMs ", 16);
        const std::string_view kScan("scan ", 5);
        const std::string_view kImage("image ", 6);

        std::uint32_t number = 0;
        bool boolean = false;
        if (StartsWith(line, "totalImages ") && ParseUInt32(std::string_view(line).substr(12), number))
        {
            parsed.totalImages = number;
        }
        else if (StartsWith(line, "offset ") && ParseUInt32(std::string_view(line).substr(7), number))
        {
            parsed.imageOffset = number;
        }
        else if (line == "paged 1")
        {
            parsed.pagedImages = true;
        }
        else if (StartsWith(line, "paused ") && ParseBool(std::string_view(line).substr(7), boolean))
        {
            parsed.transferPaused = boolean;
        }
        else if (StartsWith(line, kRevision) && ParseUInt32(std::string_view(line).substr(kRevision.size()), number))
        {
            parsed.revision = number;
        }
        else if (StartsWith(line, kDuplex) && ParseBool(std::string_view(line).substr(kDuplex.size()), boolean))
        {
            parsed.duplexEnabled = boolean;
        }
        else if (StartsWith(line, kPixel))
        {
            parsed.pixelType = Utf8ToWide(line.substr(kPixel.size()));
        }
        else if (StartsWith(line, kPaper))
        {
            parsed.paperSize = Utf8ToWide(line.substr(kPaper.size()));
        }
        else if (StartsWith(line, kXRes) && ParseUInt32(std::string_view(line).substr(kXRes.size()), number))
        {
            parsed.xResolution = number;
        }
        else if (StartsWith(line, kYRes) && ParseUInt32(std::string_view(line).substr(kYRes.size()), number))
        {
            parsed.yResolution = number;
        }
        else if (StartsWith(line, kTransferDelayMs) &&
                 ParseUInt32(std::string_view(line).substr(kTransferDelayMs.size()), number))
        {
            parsed.transferBufferDelayMilliseconds = (std::min)(number, static_cast<std::uint32_t>(5000));
        }
        else if (StartsWith(line, kScan) && ParseBool(std::string_view(line).substr(kScan.size()), boolean))
        {
            parsed.scanRequested = boolean;
        }
        else if (StartsWith(line, kImage))
        {
            mbf::twain::ScannerIpcImage image{};
            if (ParseImagePayload(std::string_view(line).substr(kImage.size()), image))
            {
                parsed.selectedImages.push_back(std::move(image));
            }
        }
    }

    if (!sawEnd)
    {
        return false;
    }

    if (!parsed.pagedImages) parsed.totalImages = static_cast<std::uint32_t>(parsed.selectedImages.size());
    if (parsed.totalImages > 100000U || parsed.imageOffset > parsed.totalImages ||
        parsed.selectedImages.size() > parsed.totalImages - parsed.imageOffset) return false;
    state = std::move(parsed);
    return true;
}

} // namespace

namespace mbf::twain
{

ScannerIpcClient::ScannerIpcClient(std::wstring pipeName)
    : pipePath_(L"\\\\.\\pipe\\" + std::move(pipeName))
{
}

bool ScannerIpcClient::Ping(DWORD timeoutMilliseconds) const
{
    std::string response;
    return SendCommand("PING\n", response, timeoutMilliseconds) && response == "OK PONG\n";
}

bool ScannerIpcClient::BeginScan(DWORD timeoutMilliseconds, DWORD* lastError) const
{
    std::string response;
    return SendCommand("BEGIN_SCAN\n", response, timeoutMilliseconds, lastError) &&
           response == "OK BEGIN_SCAN\n";
}

bool ScannerIpcClient::BeginScan(const ScannerIpcState& initialState, DWORD timeoutMilliseconds, DWORD* lastError) const
{
    std::ostringstream command;
    command << "BEGIN_SCAN"
            << " duplex=" << (initialState.duplexEnabled ? "1" : "0")
            << " pixel=" << WideToUtf8(initialState.pixelType)
            << " paper=" << WideToUtf8(initialState.paperSize)
            << " xres=" << initialState.xResolution
            << " yres=" << initialState.yResolution
            << "\n";

    std::string response;
    return SendCommand(command.str(), response, timeoutMilliseconds, lastError) &&
           response == "OK BEGIN_SCAN\n";
}

bool ScannerIpcClient::TryGetState(ScannerIpcState& state, DWORD timeoutMilliseconds) const
{
    std::string response;
    return SendCommand("GET_STATE\n", response, timeoutMilliseconds) &&
           ParseStateResponse(response, state);
}

bool ScannerIpcClient::TryGetImagePage(std::uint32_t revision, std::uint32_t offset, std::uint32_t count,
    ScannerIpcState& state, DWORD timeoutMilliseconds) const
{
    std::string response;
    const std::string command = "GET_IMAGE_PAGE " + std::to_string(revision) + " " +
        std::to_string(offset) + " " + std::to_string(count) + "\n";
    return SendCommand(command, response, timeoutMilliseconds) && ParseStateResponse(response, state) &&
        state.revision == revision && state.imageOffset == offset && state.selectedImages.size() <= count;
}

bool ScannerIpcClient::TryPrepareImage(std::uint32_t revision, std::uint32_t index, std::wstring& path, bool& skip,
    DWORD timeoutMilliseconds) const
{
    std::string response;
    skip = false;
    if (!SendCommand("GET_TRANSFER_IMAGE " + std::to_string(revision) + " " + std::to_string(index) + "\n",
        response, timeoutMilliseconds)) return false;
    if (response == "OK SKIP\n") { skip = true; return true; }
    if (!StartsWith(response, "OK IMAGE ")) return false;
    const size_t end = response.find_first_of("\r\n", 9);
    path = Utf8ToWide(response.substr(9, end == std::string::npos ? end : end - 9));
    return !path.empty();
}

bool ScannerIpcClient::ReportTransferProgress(
    std::uint32_t revision,
    std::uint32_t completedImages,
    std::uint32_t totalImages,
    DWORD timeoutMilliseconds) const
{
    std::string response;
    std::ostringstream command;
    command << "TRANSFER_PROGRESS "
            << revision << ' '
            << completedImages << ' '
            << totalImages << "\n";
    return SendCommand(command.str(), response, timeoutMilliseconds) &&
           response == "OK PROGRESS\n";
}

bool ScannerIpcClient::HideScanUi(std::uint32_t revision, DWORD timeoutMilliseconds) const
{
    std::string response;
    return SendCommand("HIDE_SCAN_UI " + std::to_string(revision) + "\n", response, timeoutMilliseconds) &&
           response == "OK HIDE\n";
}

bool ScannerIpcClient::AcknowledgeScan(std::uint32_t revision, DWORD timeoutMilliseconds) const
{
    std::string response;
    return SendCommand("ACK_SCAN " + std::to_string(revision) + "\n", response, timeoutMilliseconds) &&
           response == "OK ACK\n";
}

bool ScannerIpcClient::SendCommand(
    const std::string& command,
    std::string& response,
    DWORD timeoutMilliseconds,
    DWORD* lastError) const
{
    response.clear();
    const std::wstring commandName = CommandName(command);
    diagnostics::AppendLine(
        L"IPC",
        L"SendCommand start pipe=" + pipePath_ +
            L" command=" + commandName +
            L" timeoutMs=" + std::to_wstring(timeoutMilliseconds));

    const ULONGLONG deadline = GetTickCount64() + timeoutMilliseconds;
    HANDLE pipe = INVALID_HANDLE_VALUE;
    for (;;)
    {
        pipe = CreateFileW(
            pipePath_.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
            nullptr);
        if (pipe != INVALID_HANDLE_VALUE)
        {
            break;
        }

        const DWORD error = GetLastError();
        const ULONGLONG now = GetTickCount64();
        if (now >= deadline)
        {
            if (lastError != nullptr)
            {
                *lastError = error;
            }
            diagnostics::AppendLine(
                L"IPC",
                L"SendCommand timeout while opening pipe command=" + commandName +
                    L" lastError=" + std::to_wstring(error) +
                    L" (" + diagnostics::FormatWindowsError(error) + L")");
            return false;
        }

        const auto remaining = static_cast<DWORD>((std::min)(deadline - now, static_cast<ULONGLONG>(50)));
        if (error == ERROR_PIPE_BUSY)
        {
            diagnostics::AppendLine(
                L"IPC",
                L"Pipe busy for command=" + commandName +
                    L", waiting up to " + std::to_wstring(remaining) + L" ms");
            WaitNamedPipeW(pipePath_.c_str(), remaining);
        }
        else if (error == ERROR_FILE_NOT_FOUND)
        {
            Sleep((std::min)(remaining, static_cast<DWORD>(10)));
        }
        else
        {
            if (lastError != nullptr)
            {
                *lastError = error;
            }
            diagnostics::AppendLine(
                L"IPC",
                L"CreateFileW failed command=" + commandName +
                    L" error=" + std::to_wstring(error) +
                    L" (" + diagnostics::FormatWindowsError(error) + L")");
            return false;
        }
    }

    diagnostics::AppendLine(L"IPC", L"Connected to pipe for command=" + commandName);

    DWORD bytesWritten = 0;
    DWORD error = ERROR_SUCCESS;
    if (!TransferPipeBytes(pipe, true, const_cast<char*>(command.data()), static_cast<DWORD>(command.size()),
        bytesWritten, deadline, error) || bytesWritten != command.size())
    {
        CloseHandle(pipe);
        return false;
    }

    std::array<char, 1024> buffer{};
    for (;;)
    {
        DWORD bytesRead = 0;
        if (!TransferPipeBytes(pipe, false, buffer.data(), static_cast<DWORD>(buffer.size()), bytesRead, deadline, error))
        {
            diagnostics::AppendLine(L"IPC", L"Read completed command=" + commandName +
                L" error=" + std::to_wstring(error) + L" response=" + ResponseSnippet(response));
            CloseHandle(pipe);
            return error == ERROR_BROKEN_PIPE && !response.empty();
        }
        if (bytesRead == 0) break;
        response.append(buffer.data(), bytesRead);
        if (response.size() > 4U * 1024U * 1024U)
        {
            CloseHandle(pipe);
            return false;
        }
    }
    CloseHandle(pipe);
    return !response.empty();
}

} // namespace mbf::twain

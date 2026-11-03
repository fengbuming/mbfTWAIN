#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>

#include "twain.h"

// Use the real Windows DSM1 without injecting DAT_ENTRYPOINT. The existing
// SmokeDsEntry exercises DSM2-style injected callbacks separately.
int wmain(int argc, wchar_t** argv)
{
    const bool memory = argc == 2 && wcscmp(argv[1], L"--memory") == 0;
    const bool cancel = argc == 2 && wcscmp(argv[1], L"--cancel") == 0;
    if (argc > 2 || (argc == 2 && !memory && !cancel))
    {
        std::fprintf(stderr, "usage: SmokeLegacyDsm.exe [--memory|--cancel]\n");
        return 2;
    }

    wchar_t windowsDirectory[MAX_PATH]{};
    if (GetWindowsDirectoryW(windowsDirectory, MAX_PATH) == 0)
    {
        return 2;
    }
    std::wstring dsmPath = std::wstring(windowsDirectory) + L"\\twain_32.dll";
    HMODULE dsm = LoadLibraryW(dsmPath.c_str());
    const auto entry = dsm == nullptr
        ? nullptr
        : reinterpret_cast<DSMENTRYPROC>(GetProcAddress(dsm, "DSM_Entry"));
    if (entry == nullptr)
    {
        std::fprintf(stderr, "Could not load Windows DSM1: %lu\n", GetLastError());
        if (dsm != nullptr) FreeLibrary(dsm);
        return 2;
    }

    HWND parent = CreateWindowExW(
        0, L"STATIC", L"mbfTwain legacy DSM smoke", WS_OVERLAPPED,
        0, 0, 100, 100, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (parent == nullptr)
    {
        FreeLibrary(dsm);
        return 2;
    }

    TW_IDENTITY app{};
    app.ProtocolMajor = 1;
    app.ProtocolMinor = 9;
    app.SupportedGroups = DG_CONTROL | DG_IMAGE;
    app.Version.Language = TWLG_ENGLISH_USA;
    app.Version.Country = TWCY_USA;
    strcpy_s(app.Manufacturer, "MBF");
    strcpy_s(app.ProductFamily, "Test Host");
    strcpy_s(app.ProductName, "mbfTwain DSM1 smoke");
    TW_IDENTITY source{};
    bool dsmOpen = false;
    bool sourceOpen = false;
    bool sourceEnabled = false;
    int failures = 0;

    const auto check = [&](const char* label, TW_UINT16 rc)
    {
        std::printf("%s rc=%u\n", label, rc);
        if (rc != TWRC_SUCCESS) ++failures;
        return rc == TWRC_SUCCESS;
    };

    do
    {
        if (!check("DSM1 OPENDSM", entry(&app, nullptr, DG_CONTROL, DAT_PARENT, MSG_OPENDSM, &parent))) break;
        dsmOpen = true;
        TW_UINT16 rc = entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_GETFIRST, &source);
        while (rc == TWRC_SUCCESS && strcmp(source.ProductName, "mbf Virtual TWAIN Scanner") != 0)
        {
            rc = entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_GETNEXT, &source);
        }
        if (!check("DSM1 find mbf source", rc)) break;
        if (!check("DSM1 OPENDS", entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_OPENDS, &source))) break;
        sourceOpen = true;

        if (memory)
        {
            TW_CAPABILITY cap{};
            cap.Cap = ICAP_XFERMECH;
            cap.ConType = TWON_ONEVALUE;
            cap.hContainer = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, sizeof(TW_ONEVALUE));
            auto* value = static_cast<pTW_ONEVALUE>(GlobalLock(cap.hContainer));
            if (value == nullptr)
            {
                if (cap.hContainer != nullptr) GlobalFree(cap.hContainer);
                ++failures;
                break;
            }
            value->ItemType = TWTY_UINT16;
            value->Item = TWSX_MEMORY;
            GlobalUnlock(cap.hContainer);
            rc = entry(&app, &source, DG_CONTROL, DAT_CAPABILITY, MSG_SET, &cap);
            GlobalFree(cap.hContainer);
            if (!check("DSM1 set memory transfer", rc)) break;
        }

        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&message);
        TW_USERINTERFACE ui{};
        ui.hParent = parent;
        // Match the affected scan client: ShowUI=FALSE, MBF_TWAIN_FORCE_UI=1.
        if (!check("DSM1 ENABLEDS", entry(&app, &source, DG_CONTROL, DAT_USERINTERFACE, MSG_ENABLEDS, &ui))) break;
        sourceEnabled = true;

        TW_UINT16 notification = MSG_NULL;
        const ULONGLONG deadline = GetTickCount64() + 5000;
        while (notification == MSG_NULL && GetTickCount64() < deadline)
        {
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                TW_EVENT event{};
                event.pEvent = &message;
                rc = entry(&app, &source, DG_CONTROL, DAT_EVENT, MSG_PROCESSEVENT, &event);
                if (rc == TWRC_DSEVENT && event.TWMessage != MSG_NULL)
                {
                    notification = event.TWMessage;
                    break;
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            if (notification == MSG_NULL) Sleep(10);
        }
        const TW_UINT16 expected = cancel ? MSG_CLOSEDSREQ : MSG_XFERREADY;
        std::printf("DSM1 notification=%u expected=%u (without DAT_ENTRYPOINT)\n", notification, expected);
        if (notification != expected)
        {
            ++failures;
            break;
        }
        if (cancel) break;

        TW_IMAGEINFO info{};
        if (!check("DSM1 IMAGEINFO", entry(&app, &source, DG_IMAGE, DAT_IMAGEINFO, MSG_GET, &info))) break;
        if (info.ImageWidth <= 0 || info.ImageLength <= 0)
        {
            ++failures;
            break;
        }

        if (memory)
        {
            std::vector<BYTE> buffer(65536);
            unsigned long long bytes = 0;
            for (unsigned int block = 0; block < 65536; ++block)
            {
                TW_IMAGEMEMXFER transfer{};
                transfer.Memory.Flags = TWMF_APPOWNS | TWMF_POINTER;
                transfer.Memory.Length = static_cast<TW_UINT32>(buffer.size());
                transfer.Memory.TheMem = buffer.data();
                rc = entry(&app, &source, DG_IMAGE, DAT_IMAGEMEMXFER, MSG_GET, &transfer);
                bytes += transfer.BytesWritten;
                if (rc != TWRC_SUCCESS) break;
            }
            std::printf("DSM1 memory transfer rc=%u bytes=%llu\n", rc, bytes);
            if (rc != TWRC_XFERDONE || bytes == 0) ++failures;
        }
        else
        {
            TW_HANDLE image = nullptr;
            rc = entry(&app, &source, DG_IMAGE, DAT_IMAGENATIVEXFER, MSG_GET, &image);
            const SIZE_T bytes = image == nullptr ? 0 : GlobalSize(image);
            std::printf("DSM1 native transfer rc=%u bytes=%zu\n", rc, bytes);
            if (rc != TWRC_XFERDONE || bytes <= sizeof(BITMAPINFOHEADER)) ++failures;
            if (image != nullptr) GlobalFree(image);
        }
        if (failures != 0) break;
        TW_PENDINGXFERS pending{};
        check("DSM1 ENDXFER", entry(&app, &source, DG_CONTROL, DAT_PENDINGXFERS, MSG_ENDXFER, &pending));
        std::printf("DSM1 pending=%u\n", pending.Count);
        if (pending.Count != 0) ++failures;
    } while (false);

    if (sourceEnabled)
    {
        if (failures != 0)
        {
            TW_PENDINGXFERS pending{};
            entry(&app, &source, DG_CONTROL, DAT_PENDINGXFERS, MSG_RESET, &pending);
        }
        TW_USERINTERFACE ui{};
        ui.hParent = parent;
        check("DSM1 DISABLEDS", entry(&app, &source, DG_CONTROL, DAT_USERINTERFACE, MSG_DISABLEDS, &ui));
    }
    if (sourceOpen) check("DSM1 CLOSEDS", entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_CLOSEDS, &source));
    if (dsmOpen) check("DSM1 CLOSEDSM", entry(&app, nullptr, DG_CONTROL, DAT_PARENT, MSG_CLOSEDSM, &parent));
    DestroyWindow(parent);
    FreeLibrary(dsm);
    std::printf("DSM1 smoke: %s\n", failures == 0 ? "PASS" : "FAIL");
    return failures == 0 ? 0 : 1;
}

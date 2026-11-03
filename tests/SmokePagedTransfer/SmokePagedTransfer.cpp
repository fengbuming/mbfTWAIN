#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include "twain.h"

using DsEntry = TW_UINT16(TW_CALLINGSTYLE*)(pTW_IDENTITY, TW_UINT32, TW_UINT16, TW_UINT16, TW_MEMREF);
static std::atomic_bool ready{false};
static TW_UINT16 TW_CALLINGSTYLE Notify(pTW_IDENTITY, pTW_IDENTITY, TW_UINT32, TW_UINT16, TW_UINT16 message, TW_MEMREF)
{
    if (message == MSG_XFERREADY) ready = true;
    return TWRC_SUCCESS;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 3) return 2;
    const unsigned long expected = std::wcstoul(argv[2], nullptr, 10);
    HMODULE module = LoadLibraryW(argv[1]);
    if (module == nullptr) return 2;
    auto entry = reinterpret_cast<DsEntry>(GetProcAddress(module, "DS_Entry"));
    if (entry == nullptr) return 2;
    TW_IDENTITY app{};
    app.Id = 41;
    app.ProtocolMajor = 2;
    app.ProtocolMinor = 4;
    app.SupportedGroups = DG_CONTROL | DG_IMAGE | DF_APP2;
    TW_IDENTITY source{};
    TW_ENTRYPOINT point{};
    point.Size = sizeof(point);
    point.DSM_Entry = Notify;
    if (entry(&app, DG_CONTROL, DAT_IDENTITY, MSG_GET, &source) != TWRC_SUCCESS) return 3;
    source.Id = 17;
    if (entry(&app, DG_CONTROL, DAT_ENTRYPOINT, MSG_SET, &point) != TWRC_SUCCESS ||
        entry(&app, DG_CONTROL, DAT_IDENTITY, MSG_OPENDS, &source) != TWRC_SUCCESS) return 3;
    TW_USERINTERFACE ui{};
    ui.ShowUI = TRUE;
    if (entry(&app, DG_CONTROL, DAT_USERINTERFACE, MSG_ENABLEDS, &ui) != TWRC_SUCCESS) return 3;
    const ULONGLONG deadline = GetTickCount64() + 5000;
    while (!ready && GetTickCount64() < deadline) Sleep(10);
    if (!ready) return 4;
    TW_EVENT event{};
    entry(&app, DG_CONTROL, DAT_EVENT, MSG_PROCESSEVENT, &event);
    TW_PENDINGXFERS pending{};
    if (entry(&app, DG_CONTROL, DAT_PENDINGXFERS, MSG_GET, &pending) != TWRC_SUCCESS) return 4;
    unsigned long delivered = 0;
    bool valid = true;
    while (pending.Count > 0)
    {
        TW_IMAGEINFO info{};
        const TW_UINT16 infoResult = entry(&app, DG_IMAGE, DAT_IMAGEINFO, MSG_GET, &info);
        if (infoResult == TWRC_CANCEL)
        {
            entry(&app, DG_CONTROL, DAT_PENDINGXFERS, MSG_ENDXFER, &pending);
            break;
        }
        if (infoResult != TWRC_SUCCESS) { valid = false; break; }
        TW_HANDLE dib = nullptr;
        const TW_UINT16 transferred = entry(&app, DG_IMAGE, DAT_IMAGENATIVEXFER, MSG_GET, &dib);
        if (transferred == TWRC_CANCEL)
        {
            entry(&app, DG_CONTROL, DAT_PENDINGXFERS, MSG_ENDXFER, &pending);
            break;
        }
        if (transferred != TWRC_XFERDONE || dib == nullptr) { valid = false; break; }
        GlobalFree(dib);
        ++delivered;
        std::printf("Delivered page %lu\n", delivered);
        if (entry(&app, DG_CONTROL, DAT_PENDINGXFERS, MSG_ENDXFER, &pending) != TWRC_SUCCESS)
        { valid = false; break; }
    }
    const bool passed = valid && pending.Count == 0 && delivered == expected;
    std::printf("Paged issue recovery: %s delivered=%lu expected=%lu remaining=%u\n",
        passed ? "PASS" : "FAIL", delivered, expected, pending.Count);
    if (entry(&app, DG_CONTROL, DAT_USERINTERFACE, MSG_DISABLEDS, &ui) != TWRC_SUCCESS) valid = false;
    if (entry(&app, DG_CONTROL, DAT_IDENTITY, MSG_CLOSEDS, &source) != TWRC_SUCCESS) valid = false;
    FreeLibrary(module);
    return passed && valid ? 0 : 1;
}

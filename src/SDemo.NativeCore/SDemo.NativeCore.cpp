// SDemo.NativeCore.cpp
// SDemo.QuickTranslate 原生库 - 纯 Win32 部分（模拟复制、剪贴板、屏幕截图、窗口/光标信息）。
#include <windows.h>
#include <wchar.h>
#include <cstdio>
#include <string>
#include <vector>
#include "SDemo.NativeCore.h"

namespace {
thread_local std::wstring g_lastError;
void SetError(const wchar_t* m) { g_lastError = m ? m : L""; }

// 将 top-down 32bpp BGRA 像素写入 BMP 文件
bool WriteBmpFile(const wchar_t* path, int w, int h, const std::vector<BYTE>& bits)
{
    BITMAPFILEHEADER bfh = {};
    bfh.bfType = 0x4D42; // 'BM'
    bfh.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
    bfh.bfSize = bfh.bfOffBits + (DWORD)bits.size();

    BITMAPINFOHEADER bi = {};
    bi.biSize = sizeof(BITMAPINFOHEADER);
    bi.biWidth = w;
    bi.biHeight = -h; // top-down
    bi.biPlanes = 1;
    bi.biBitCount = 32;
    bi.biCompression = BI_RGB;
    bi.biSizeImage = (DWORD)bits.size();

    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") != 0 || !f) return false;
    bool ok = (fwrite(&bfh, 1, sizeof(bfh), f) == sizeof(bfh)) &&
              (fwrite(&bi, 1, sizeof(bi), f) == sizeof(bi)) &&
              (fwrite(bits.data(), 1, bits.size(), f) == bits.size());
    fclose(f);
    return ok;
}
} // namespace

extern "C" {

SDEMO_API const char* SDemoVersion(void) { return "1.0.0"; }

SDEMO_API const wchar_t* SDemoLastError(void) { return g_lastError.c_str(); }

SDEMO_API int SDemoCopySelection(void)
{
    SetError(L"");
    keybd_event(VK_CONTROL, 0, 0, 0);
    keybd_event('C', 0, 0, 0);
    keybd_event('C', 0, KEYEVENTF_KEYUP, 0);
    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    return 0;
}

SDEMO_API int SDemoGetClipboardText(wchar_t* buf, int cap)
{
    SetError(L"");
    if (!buf || cap <= 0) { SetError(L"invalid buffer"); return -1; }
    buf[0] = L'\0';
    if (!OpenClipboard(nullptr)) { SetError(L"OpenClipboard failed"); return -1; }

    int rc = 1; // 默认无文本
    HANDLE h = GetClipboardData(CF_UNICODETEXT);
    if (h)
    {
        const wchar_t* p = (const wchar_t*)GlobalLock(h);
        if (p)
        {
            wcsncpy_s(buf, (size_t)cap, p, _TRUNCATE);
            buf[cap - 1] = L'\0';
            rc = (buf[0] != L'\0') ? 0 : 1;
            GlobalUnlock(h);
        }
    }
    CloseClipboard();
    return rc;
}

SDEMO_API int SDemoCaptureScreenToBmp(int x, int y, int w, int h, const wchar_t* outBmpPath)
{
    SetError(L"");
    if (w <= 0 || h <= 0 || !outBmpPath) { SetError(L"invalid args"); return -1; }

    HDC screen = GetDC(nullptr);
    if (!screen) { SetError(L"GetDC failed"); return -1; }
    HDC mem = CreateCompatibleDC(screen);
    HBITMAP bmp = CreateCompatibleBitmap(screen, w, h);
    HGDIOBJ old = SelectObject(mem, bmp);

    int rc = -1;
    if (BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT))
    {
        std::vector<BYTE> bits((size_t)w * (size_t)h * 4);
        BITMAPINFO bmi = {};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        int lines = GetDIBits(mem, bmp, 0, (UINT)h, bits.data(), &bmi, DIB_RGB_COLORS);
        if (lines == h && WriteBmpFile(outBmpPath, w, h, bits)) rc = 0;
        else SetError(L"GetDIBits/WriteBmpFile failed");
    }
    else SetError(L"BitBlt failed");

    SelectObject(mem, old);
    DeleteObject(bmp);
    DeleteDC(mem);
    ReleaseDC(nullptr, screen);
    return rc;
}

SDEMO_API int SDemoGetCursorPos(int* x, int* y)
{
    POINT pt = {};
    if (!GetCursorPos(&pt)) { SetError(L"GetCursorPos failed"); return -1; }
    if (x) *x = pt.x;
    if (y) *y = pt.y;
    return 0;
}

SDEMO_API int SDemoGetWindowAtPoint(int x, int y, void** hwnd)
{
    POINT pt = { x, y };
    HWND h = WindowFromPoint(pt);
    if (h)
    {
        HWND child = ChildWindowFromPointEx(h, pt, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
        if (child) h = child;
    }
    if (hwnd) *hwnd = h;
    return h ? 0 : 1;
}

SDEMO_API int SDemoGetForegroundWindowRect(int* x, int* y, int* w, int* h)
{
    HWND fg = GetForegroundWindow();
    if (!fg) { SetError(L"no foreground window"); return -1; }
    RECT r = {};
    GetWindowRect(fg, &r);
    if (x) *x = r.left;
    if (y) *y = r.top;
    if (w) *w = r.right - r.left;
    if (h) *h = r.bottom - r.top;
    return 0;
}

SDEMO_API int SDemoSaveClipboardBitmap(const wchar_t* outBmpPath)
{
    SetError(L"");
    if (!outBmpPath) { SetError(L"invalid args"); return -1; }
    if (!OpenClipboard(nullptr)) { SetError(L"OpenClipboard failed"); return -1; }

    int rc = 1; // 默认无图片
    HANDLE h = GetClipboardData(CF_DIB);
    if (h)
    {
        const BYTE* dib = (const BYTE*)GlobalLock(h);
        SIZE_T size = GlobalSize(h);
        if (dib && size >= sizeof(BITMAPINFOHEADER))
        {
            const BITMAPINFOHEADER* bi = (const BITMAPINFOHEADER*)dib;
            if (bi->biSize >= sizeof(BITMAPINFOHEADER) &&
                (bi->biCompression == BI_RGB || bi->biCompression == BI_BITFIELDS))
            {
                int w = bi->biWidth;
                int hgt = (bi->biHeight < 0) ? -bi->biHeight : bi->biHeight;
                DWORD bpp = bi->biBitCount;
                DWORD maskSize = (bi->biCompression == BI_BITFIELDS && bpp == 32) ? 12 : 0;

                void* pvBits = nullptr;
                HBITMAP hbmp = CreateDIBSection(nullptr, (BITMAPINFO*)dib, DIB_RGB_COLORS, &pvBits, nullptr, 0);
                if (hbmp && pvBits)
                {
                    const BYTE* src = dib + bi->biSize + maskSize;
                    size_t row = ((size_t)w * bpp + 31) / 32 * 4;
                    size_t copyLen = row * (size_t)hgt;
                    if (src + copyLen <= dib + size)
                    {
                        memcpy(pvBits, src, copyLen);

                        std::vector<BYTE> outBits((size_t)w * (size_t)hgt * 4);
                        BITMAPINFO outBmi = {};
                        outBmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                        outBmi.bmiHeader.biWidth = w;
                        outBmi.bmiHeader.biHeight = -hgt;
                        outBmi.bmiHeader.biPlanes = 1;
                        outBmi.bmiHeader.biBitCount = 32;
                        outBmi.bmiHeader.biCompression = BI_RGB;

                        HDC dc = GetDC(nullptr);
                        HDC memdc = CreateCompatibleDC(dc);
                        HGDIOBJ old = SelectObject(memdc, hbmp);
                        int lines = GetDIBits(memdc, hbmp, 0, (UINT)hgt, outBits.data(), &outBmi, DIB_RGB_COLORS);
                        SelectObject(memdc, old);
                        DeleteDC(memdc);
                        ReleaseDC(nullptr, dc);

                        if (lines == hgt && WriteBmpFile(outBmpPath, w, hgt, outBits)) rc = 0;
                        else SetError(L"convert failed");
                    }
                    else SetError(L"clipboard data truncated");
                }
                else SetError(L"CreateDIBSection failed");
                if (hbmp) DeleteObject(hbmp);
            }
            else SetError(L"unsupported DIB format");
        }
        else SetError(L"invalid DIB");
        GlobalUnlock(h);
    }
    CloseClipboard();
    return rc;
}

} // extern "C"

// NativeCore.h
// QuickTranslate 的 C++ 原生库接口（C ABI，供 C# 通过 P/Invoke 调用）。
// 该库负责系统级操作：模拟复制选中内容、读取剪贴板、屏幕截图、
// 光标/窗口信息，以及基于 Windows.Media.Ocr (C++/WinRT) 的文字识别。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#define QTNATIVE_API __declspec(dllexport)

// 库版本号
QTNATIVE_API const char* QtVersion(void);

// 上一次错误信息（线程局部，宽字符）
QTNATIVE_API const wchar_t* QtLastError(void);

// 模拟 Ctrl+C 复制当前选中内容。返回 0=成功, 负=失败
QTNATIVE_API int QtCopySelection(void);

// 读取剪贴板文本（CF_UNICODETEXT）到 buf（宽字符）。
// 返回 0=有文本, 1=无文本, 负=错误
QTNATIVE_API int QtGetClipboardText(wchar_t* buf, int cap);

// 将剪贴板图片（CF_DIB）保存为 BMP 文件。
// 返回 0=成功, 1=无图片, 负=错误
QTNATIVE_API int QtSaveClipboardBitmap(const wchar_t* outBmpPath);

// 截取屏幕区域 (x,y,w,h，物理像素) 保存为 32bpp BMP 文件。
// 返回 0=成功, 负=错误
QTNATIVE_API int QtCaptureScreenToBmp(int x, int y, int w, int h, const wchar_t* outBmpPath);

// 使用 Windows.Media.Ocr 识别图片文件（PNG/BMP/JPEG）中的文字。
// 返回 0=成功且有文字, 1=成功但无文字, -2=无 OCR 引擎（未安装语言包）, 其他负=错误
QTNATIVE_API int QtOcrFromImageFile(const wchar_t* imagePath, wchar_t* outBuf, int outCap);

// 获取鼠标位置（物理像素）。返回 0=成功
QTNATIVE_API int QtGetCursorPos(int* x, int* y);

// 获取指定点下的窗口句柄。返回 0=成功, 1=无窗口
QTNATIVE_API int QtGetWindowAtPoint(int x, int y, void** hwnd);

// 获取前台窗口屏幕矩形（物理像素）。返回 0=成功
QTNATIVE_API int QtGetForegroundWindowRect(int* x, int* y, int* w, int* h);

#ifdef __cplusplus
}
#endif

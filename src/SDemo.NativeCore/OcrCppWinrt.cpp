// OcrCppWinrt.cpp
// SDemo.QuickTranslate 原生库 - 基于 Windows.Media.Ocr (C++/WinRT) 的本地文字识别。
// 需要 C++20（标准协程）编译，链接 windowsapp.lib（cppwinrt 头文件自动 #pragma 引用）。
#include <windows.h>
#include <combaseapi.h> // CoInitializeEx / COINIT_MULTITHREADED
#include <wchar.h>
#include <string>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Media.Ocr.h>

#include "SDemo.NativeCore.h"

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Graphics::Imaging;
using namespace Windows::Storage;
using namespace Windows::Storage::Streams;
using namespace Windows::Media::Ocr;

extern "C" {

SDEMO_API int SDemoOcrFromImageFile(const wchar_t* imagePath, wchar_t* outBuf, int outCap)
{
    if (!outBuf || outCap <= 0) return -1;
    outBuf[0] = L'\0';
    if (!imagePath || !*imagePath) return -1;

    try
    {
        // 忽略返回值：线程可能已初始化（S_FALSE / RPC_E_CHANGED_MODE 均属正常）。
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        StorageFile file = StorageFile::GetFileFromPathAsync(std::wstring(imagePath)).get();
        if (!file) return -3;
        auto stream = file.OpenAsync(FileAccessMode::Read).get();
        BitmapDecoder decoder = BitmapDecoder::CreateAsync(stream).get();
        if (!decoder) return -3;
        SoftwareBitmap bitmap = decoder.GetSoftwareBitmapAsync().get();

        OcrEngine engine = OcrEngine::TryCreateFromUserProfileLanguages();
        if (!engine) return -2; // OCR 引擎不可用（未安装语言包）

        OcrResult result = engine.RecognizeAsync(bitmap).get();
        std::wstring text;
        for (auto const& line : result.Lines())
        {
            if (!text.empty()) text += L"\r\n";
            text += std::wstring(line.Text().c_str());
        }
        if (text.empty()) return 1; // 成功但无文字

        wcsncpy_s(outBuf, (size_t)outCap, text.c_str(), _TRUNCATE);
        outBuf[outCap - 1] = L'\0';
        return 0;
    }
    catch (...)
    {
        return -3;
    }
}

} // extern "C"

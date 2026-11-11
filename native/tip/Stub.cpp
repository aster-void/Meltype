// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 aster-void
//
// spike (#1670): COM に登録する DLL を stub にし、TextService を持つ DLL (impl) を、アプリを起動し直さずに入れ替えられるかを確かめる。
// stub は <stub のフォルダー>\current.txt に書かれた名前の DLL を <stub のフォルダー>\impl から読み込み、
// 1 秒ごとに current.txt を読み直して、名前が変わっていたら同じ ITfThreadMgr・TfClientId のまま impl を入れ替える。
// 読み込んだ impl は FreeLibrary しない。

#include <new>

#include "Globals.h"

namespace meltype {
namespace {

std::wstring StubFolder() {
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(g_module, path, MAX_PATH);
    std::wstring folder = path;
    return folder.substr(0, folder.find_last_of(L'\\'));
}

// current.txt に書かれた impl のファイル名。読めない・フォルダーの区切りを含むなら空
std::wstring CurrentImpl() {
    HANDLE file = CreateFileW((StubFolder() + L"\\current.txt").c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return {};
    char buffer[256] = {};
    DWORD read = 0;
    ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    CloseHandle(file);
    std::string text(buffer, read);
    // BOM と前後の空白・改行を除く
    if (text.rfind("\xEF\xBB\xBF", 0) == 0) text.erase(0, 3);
    const char* blank = " \t\r\n";
    size_t first = text.find_first_not_of(blank);
    if (first == std::string::npos) return {};
    text = text.substr(first, text.find_last_not_of(blank) - first + 1);
    if (text.find_first_of("\\/:") != std::string::npos) return {};
    return FromUtf8(text);
}

ITfTextInputProcessorEx* CreateInner(const std::wstring& name) {
    if (name.empty()) return nullptr;
    std::wstring path = StubFolder() + L"\\impl\\" + name;
    HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr) {
        TipLog(L"stub: %s を読み込めません: %lu", path.c_str(), GetLastError());
        return nullptr;
    }
    using GetClassObject = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
    auto get = reinterpret_cast<GetClassObject>(reinterpret_cast<void*>(GetProcAddress(module, "DllGetClassObject")));
    IClassFactory* factory = nullptr;
    if (get == nullptr || FAILED(get(CLSID_TextService, IID_IClassFactory, reinterpret_cast<void**>(&factory)))) {
        TipLog(L"stub: %s に DllGetClassObject がありません", path.c_str());
        return nullptr;
    }
    ITfTextInputProcessorEx* inner = nullptr;
    HRESULT hr = factory->CreateInstance(nullptr, IID_ITfTextInputProcessorEx, reinterpret_cast<void**>(&inner));
    factory->Release();
    TipLog(L"stub: %s の TextService を作りました: 0x%08lX (module %p)", name.c_str(), hr, module);
    return inner;
}

constexpr wchar_t kWindowClass[] = L"Meltype.Tip.Stub";
constexpr UINT_PTR kPollTimer = 1;

class StubService final : public ITfTextInputProcessorEx, public ITfDisplayAttributeProvider {
public:
    StubService() : loaded_(CurrentImpl()), inner_(CreateInner(loaded_)) { DllAddRef(); }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (ppv == nullptr) return E_INVALIDARG;
        *ppv = nullptr;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITfTextInputProcessor) || IsEqualIID(riid, IID_ITfTextInputProcessorEx)) {
            *ppv = static_cast<ITfTextInputProcessorEx*>(this);
        } else if (IsEqualIID(riid, IID_ITfDisplayAttributeProvider)) {
            *ppv = static_cast<ITfDisplayAttributeProvider*>(this);
        }
        if (*ppv == nullptr) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs_); }
    STDMETHODIMP_(ULONG) Release() override {
        ULONG count = InterlockedDecrement(&refs_);
        if (count == 0) delete this;
        return count;
    }

    // ITfTextInputProcessorEx
    STDMETHODIMP Activate(ITfThreadMgr* threadMgr, TfClientId clientId) override { return ActivateEx(threadMgr, clientId, 0); }
    STDMETHODIMP ActivateEx(ITfThreadMgr* threadMgr, TfClientId clientId, DWORD flags) override {
        if (inner_ == nullptr) return E_FAIL;
        threadMgr_ = threadMgr;
        threadMgr_->AddRef();
        clientId_ = clientId;
        flags_ = flags;
        HRESULT hr = inner_->ActivateEx(threadMgr_, clientId_, flags_);
        TipLog(L"stub: Activate %s: 0x%08lX", loaded_.c_str(), hr);
        WNDCLASSW cls{};
        cls.lpfnWndProc = WindowProc;
        cls.hInstance = g_module;
        cls.lpszClassName = kWindowClass;
        RegisterClassW(&cls);
        window_ = CreateWindowExW(0, kWindowClass, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_module, nullptr);
        if (window_) {
            SetWindowLongPtrW(window_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
            SetTimer(window_, kPollTimer, 1000, nullptr);
        }
        return hr;
    }
    STDMETHODIMP Deactivate() override {
        if (window_) {
            DestroyWindow(window_);
            window_ = nullptr;
        }
        HRESULT hr = inner_ ? inner_->Deactivate() : S_OK;
        TipLog(L"stub: Deactivate %s: 0x%08lX", loaded_.c_str(), hr);
        if (threadMgr_) {
            threadMgr_->Release();
            threadMgr_ = nullptr;
        }
        return hr;
    }

    // ITfDisplayAttributeProvider
    STDMETHODIMP EnumDisplayAttributeInfo(IEnumTfDisplayAttributeInfo** ppEnum) override {
        ITfDisplayAttributeProvider* provider = Provider();
        if (provider == nullptr) return E_FAIL;
        HRESULT hr = provider->EnumDisplayAttributeInfo(ppEnum);
        provider->Release();
        return hr;
    }
    STDMETHODIMP GetDisplayAttributeInfo(REFGUID guid, ITfDisplayAttributeInfo** ppInfo) override {
        ITfDisplayAttributeProvider* provider = Provider();
        if (provider == nullptr) return E_FAIL;
        HRESULT hr = provider->GetDisplayAttributeInfo(guid, ppInfo);
        provider->Release();
        return hr;
    }

private:
    ~StubService() {
        if (inner_) inner_->Release();
        DllRelease();
    }

    ITfDisplayAttributeProvider* Provider() {
        ITfDisplayAttributeProvider* provider = nullptr;
        if (inner_) inner_->QueryInterface(IID_ITfDisplayAttributeProvider, reinterpret_cast<void**>(&provider));
        return provider;
    }

    static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<StubService*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_TIMER && wParam == kPollTimer && self != nullptr) {
            self->Poll();
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    void Poll() {
        std::wstring name = CurrentImpl();
        if (name.empty() || name == loaded_ || name == failed_) return;
        TipLog(L"stub: 入れ替えます: %s → %s", loaded_.c_str(), name.c_str());
        ITfTextInputProcessorEx* next = CreateInner(name);
        if (next == nullptr) {
            failed_ = name;
            return;
        }
        // 入れ替えの途中で TSF が Release しても消えないようにする
        AddRef();
        HRESULT hr = inner_->Deactivate();
        TipLog(L"stub: Deactivate %s: 0x%08lX", loaded_.c_str(), hr);
        ULONG left = inner_->Release();
        TipLog(L"stub: %s の TextService に残った参照: %lu", loaded_.c_str(), left);
        inner_ = next;
        loaded_ = name;
        hr = inner_->ActivateEx(threadMgr_, clientId_, flags_);
        TipLog(L"stub: Activate %s: 0x%08lX", loaded_.c_str(), hr);
        Release();
    }

    LONG refs_ = 1;
    std::wstring loaded_;  // inner_ を作った impl のファイル名
    std::wstring failed_;  // 読み込めなかった impl のファイル名 (1 秒ごとに試し直さない)
    ITfTextInputProcessorEx* inner_ = nullptr;
    ITfThreadMgr* threadMgr_ = nullptr;
    TfClientId clientId_ = TF_CLIENTID_NULL;
    DWORD flags_ = 0;
    HWND window_ = nullptr;
};

}  // namespace

HRESULT CreateStubService(REFIID riid, void** ppv) {
    auto* service = new (std::nothrow) StubService();
    if (service == nullptr) return E_OUTOFMEMORY;
    HRESULT hr = service->QueryInterface(riid, ppv);
    service->Release();
    return hr;
}

}  // namespace meltype

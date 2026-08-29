#include <Windows.h>
#include <ShlObj_core.h>
#include <ShObjIdl_core.h>
#include <shellapi.h>
#include <Shlwapi.h>
#include <strsafe.h>
#include <winreg.h>

using SizeType = decltype(sizeof(0));

void* operator new(SizeType, void* address) noexcept
{
    return address;
}

#include "resource.h"

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")

namespace
{
    template<typename T>
    T* AllocateObject()
    {
        void* memory = HeapAlloc(GetProcessHeap(), 0, sizeof(T));
        return memory == nullptr ? nullptr : new (memory) T();
    }

    template<typename T>
    void FreeObject(T* object)
    {
        object->~T();
        HeapFree(GetProcessHeap(), 0, object);
    }

    constexpr CLSID FluenityHubExplorerCommandClsid =
    { 0xa4a6d9d9, 0x8f52, 0x49f0, { 0x94, 0xaa, 0x7c, 0x52, 0xe3, 0xd5, 0x70, 0x01 } };

    HMODULE g_module = nullptr;
    long g_objectCount = 0;
    long g_lockCount = 0;

    bool IsWindows11OrGreater()
    {
        const auto ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll == nullptr)
        {
            return false;
        }

        using RtlGetVersionFunction = LONG(WINAPI*)(OSVERSIONINFOW*);
        const auto rtlGetVersion = reinterpret_cast<RtlGetVersionFunction>(
            GetProcAddress(ntdll, "RtlGetVersion"));
        if (rtlGetVersion == nullptr)
        {
            return false;
        }

        OSVERSIONINFOW version;
        version.dwOSVersionInfoSize = sizeof(version);
        return rtlGetVersion(&version) == 0
            && (version.dwMajorVersion > 10
                || (version.dwMajorVersion == 10 && version.dwBuildNumber >= 22000));
    }

    bool IsCommandEnabled()
    {
        if (!IsWindows11OrGreater())
        {
            return false;
        }
        DWORD enabled = 0;
        DWORD size = sizeof(enabled);
        const auto result = RegGetValueW(
            HKEY_CURRENT_USER,
            L"Software\\FluenityHub\\ExplorerCommand",
            L"Enabled",
            RRF_RT_REG_DWORD,
            nullptr,
            &enabled,
            &size);
        return result == ERROR_SUCCESS && enabled != 0;
    }

    HRESULT GetSingleFolderPath(IShellItemArray* items, PWSTR* folderPath)
    {
        if (folderPath == nullptr)
        {
            return E_POINTER;
        }

        *folderPath = nullptr;
        if (items == nullptr)
        {
            return E_INVALIDARG;
        }

        DWORD count = 0;
        HRESULT result = items->GetCount(&count);
        if (FAILED(result) || count != 1)
        {
            return FAILED(result) ? result : E_INVALIDARG;
        }

        IShellItem* item = nullptr;
        result = items->GetItemAt(0, &item);
        if (FAILED(result))
        {
            return result;
        }

        result = item->GetDisplayName(SIGDN_FILESYSPATH, folderPath);
        item->Release();
        return result;
    }

    bool IsUnityProjectFolder(PCWSTR folderPath)
    {
        if (folderPath == nullptr || folderPath[0] == L'\0')
        {
            return false;
        }

        const DWORD folderAttributes = GetFileAttributesW(folderPath);
        if (folderAttributes == INVALID_FILE_ATTRIBUTES
            || (folderAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            return false;
        }

        constexpr wchar_t marker[] = L"ProjectSettings\\ProjectVersion.txt";
        const int folderLengthValue = lstrlenW(folderPath);
        if (folderLengthValue <= 0)
        {
            return false;
        }

        const SizeType folderLength = static_cast<SizeType>(folderLengthValue);
        const bool needsSeparator = folderPath[folderLength - 1] != L'\\'
            && folderPath[folderLength - 1] != L'/';
        const SizeType markerPathLength = folderLength
            + (needsSeparator ? 1 : 0)
            + ARRAYSIZE(marker);
        if (markerPathLength > STRSAFE_MAX_CCH)
        {
            return false;
        }

        auto* markerPath = static_cast<wchar_t*>(
            HeapAlloc(GetProcessHeap(), 0, markerPathLength * sizeof(wchar_t)));
        if (markerPath == nullptr)
        {
            return false;
        }

        HRESULT result = StringCchCopyW(markerPath, markerPathLength, folderPath);
        if (SUCCEEDED(result) && needsSeparator)
        {
            result = StringCchCatW(markerPath, markerPathLength, L"\\");
        }
        if (SUCCEEDED(result))
        {
            result = StringCchCatW(markerPath, markerPathLength, marker);
        }

        const DWORD markerAttributes = SUCCEEDED(result)
            ? GetFileAttributesW(markerPath)
            : INVALID_FILE_ATTRIBUTES;
        HeapFree(GetProcessHeap(), 0, markerPath);
        return markerAttributes != INVALID_FILE_ATTRIBUTES
            && (markerAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    bool GetModulePath(wchar_t (&path)[MAX_PATH])
    {
        const auto length = GetModuleFileNameW(g_module, path, MAX_PATH);
        return length != 0 && length < MAX_PATH;
    }

    bool GetApplicationPath(wchar_t (&path)[MAX_PATH])
    {
        const auto length = GetModuleFileNameW(g_module, path, MAX_PATH);
        if (length == 0 || length == MAX_PATH || !PathRemoveFileSpecW(path))
        {
            return false;
        }

        return PathAppendW(path, L"FluenityHub.exe") != FALSE;
    }

    class ExplorerCommand final : public IExplorerCommand
    {
    public:
        ExplorerCommand()
        {
            InterlockedIncrement(&g_objectCount);
        }

        ~ExplorerCommand()
        {
            InterlockedDecrement(&g_objectCount);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
            {
                *object = static_cast<IExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }

            *object = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const auto count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                FreeObject(this);
            }

            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* title) override
        {
            if (title == nullptr)
            {
                return E_POINTER;
            }

            return SHStrDupW(L"Open in FluenityHub", title);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
        {
            if (icon == nullptr)
            {
                return E_POINTER;
            }

            wchar_t path[MAX_PATH];
            if (!GetModulePath(path)
                || FAILED(StringCchCatW(path, MAX_PATH, L",-101")))
            {
                *icon = nullptr;
                return E_FAIL;
            }

            return SHStrDupW(path, icon);
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tooltip) override
        {
            if (tooltip == nullptr)
            {
                return E_POINTER;
            }

            *tooltip = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetCanonicalName(GUID* canonicalName) override
        {
            if (canonicalName == nullptr)
            {
                return E_POINTER;
            }

            *canonicalName = GUID_NULL;
            return S_OK;
        }

        IFACEMETHODIMP GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* state) override
        {
            if (state == nullptr)
            {
                return E_POINTER;
            }

            *state = ECS_HIDDEN;
            if (!IsCommandEnabled())
            {
                return S_OK;
            }

            if (!okToBeSlow)
            {
                return E_PENDING;
            }

            PWSTR folderPath = nullptr;
            const HRESULT result = GetSingleFolderPath(items, &folderPath);
            *state = SUCCEEDED(result) && IsUnityProjectFolder(folderPath)
                ? ECS_ENABLED
                : ECS_HIDDEN;
            CoTaskMemFree(folderPath);
            return S_OK;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            if (items == nullptr)
            {
                return E_INVALIDARG;
            }

            PWSTR itemPath = nullptr;
            HRESULT result = GetSingleFolderPath(items, &itemPath);
            if (FAILED(result))
            {
                return result;
            }

            if (!IsUnityProjectFolder(itemPath))
            {
                CoTaskMemFree(itemPath);
                return HRESULT_FROM_WIN32(ERROR_BAD_PATHNAME);
            }

            wchar_t applicationPath[MAX_PATH];
            if (!GetApplicationPath(applicationPath))
            {
                CoTaskMemFree(itemPath);
                return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
            }

            const auto argumentLength = lstrlenW(itemPath) + 15;
            auto* arguments = static_cast<wchar_t*>(
                HeapAlloc(GetProcessHeap(), 0, argumentLength * sizeof(wchar_t)));
            if (arguments == nullptr)
            {
                CoTaskMemFree(itemPath);
                return E_OUTOFMEMORY;
            }

            result = StringCchCopyW(arguments, argumentLength, L"--project \"");
            if (SUCCEEDED(result))
            {
                result = StringCchCatW(arguments, argumentLength, itemPath);
            }
            if (SUCCEEDED(result))
            {
                result = StringCchCatW(arguments, argumentLength, L"\"");
            }
            CoTaskMemFree(itemPath);
            if (FAILED(result))
            {
                HeapFree(GetProcessHeap(), 0, arguments);
                return result;
            }

            const auto launchResult = ShellExecuteW(
                nullptr,
                L"open",
                applicationPath,
                arguments,
                nullptr,
                SW_SHOWNORMAL);
            HeapFree(GetProcessHeap(), 0, arguments);
            return reinterpret_cast<INT_PTR>(launchResult) > 32
                ? S_OK
                : HRESULT_FROM_WIN32(ERROR_OPEN_FAILED);
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            if (flags == nullptr)
            {
                return E_POINTER;
            }

            *flags = ECF_DEFAULT;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
        {
            if (commands == nullptr)
            {
                return E_POINTER;
            }

            *commands = nullptr;
            return E_NOTIMPL;
        }

    private:
        long _referenceCount = 1;
    };

    class ExplorerCommandClassFactory final : public IClassFactory
    {
    public:
        ExplorerCommandClassFactory()
        {
            InterlockedIncrement(&g_objectCount);
        }

        ~ExplorerCommandClassFactory()
        {
            InterlockedDecrement(&g_objectCount);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *object = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            *object = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const auto count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                FreeObject(this);
            }

            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override
        {
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto* command = AllocateObject<ExplorerCommand>();
            if (command == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            const auto result = command->QueryInterface(riid, object);
            command->Release();
            return result;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
            {
                InterlockedIncrement(&g_lockCount);
            }
            else
            {
                InterlockedDecrement(&g_lockCount);
            }

            return S_OK;
        }

    private:
        long _referenceCount = 1;
    };
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 && g_lockCount == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    if (!IsEqualCLSID(clsid, FluenityHubExplorerCommandClsid))
    {
        *object = nullptr;
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = AllocateObject<ExplorerCommandClassFactory>();
    if (factory == nullptr)
    {
        *object = nullptr;
        return E_OUTOFMEMORY;
    }

    const auto result = factory->QueryInterface(riid, object);
    factory->Release();
    return result;
}
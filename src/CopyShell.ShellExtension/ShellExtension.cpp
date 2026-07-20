#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <array>
#include <atomic>
#include <cstring>
#include <filesystem>
#include <new>
#include <string>
#include <string_view>
#include <vector>

namespace
{
    // {6D5FE7D6-4A85-4ED1-AF8A-4E6F338C3D71}
    constexpr CLSID kClassId{
        0x6d5fe7d6,
        0x4a85,
        0x4ed1,
        {0xaf, 0x8a, 0x4e, 0x6f, 0x33, 0x8c, 0x3d, 0x71}};
    constexpr wchar_t kClassIdText[] = L"{6D5FE7D6-4A85-4ED1-AF8A-4E6F338C3D71}";
    constexpr wchar_t kDisplayName[] = L"CopyShell";
    constexpr GUID kCopyCommandId{
        0x5d63327e,
        0xf9d8,
        0x46db,
        {0xa4, 0xee, 0x18, 0xd8, 0x22, 0xe5, 0xd3, 0x9f}};
    constexpr GUID kMoveCommandId{
        0xec299f1d,
        0xf09d,
        0x47af,
        {0xa8, 0x93, 0xbf, 0xa4, 0x83, 0x85, 0x13, 0x21}};
    constexpr GUID kSyncCommandId{
        0xa9d21b0d,
        0xb424,
        0x46f4,
        {0xb0, 0x50, 0x61, 0x84, 0xc4, 0x95, 0xd4, 0x7e}};
    constexpr size_t kMaximumRequestBytes = 1024 * 1024;
    constexpr UINT kMaximumSources = 4096;
    constexpr UINT kCommandCount = 3;

    HMODULE g_module{};
    std::atomic<long> g_objectCount{};

    struct CommandDefinition
    {
        const wchar_t* label;
        const char* verb;
        const wchar_t* help;
        const wchar_t* operation;
    };

    constexpr std::array<CommandDefinition, kCommandCount> kCommands{{
        {L"高级复制到…", "copyshell.copy", L"使用 Robocopy 复制所选项目", L"copy"},
        {L"高级移动到…", "copyshell.move", L"使用 Robocopy 移动所选项目", L"move"},
        {L"同步到…", "copyshell.sync", L"使用 Robocopy 镜像同步所选文件夹", L"sync"},
    }};

    constexpr std::array<GUID, kCommandCount> kCommandIds{{
        kCopyCommandId,
        kMoveCommandId,
        kSyncCommandId,
    }};

    std::wstring GetModulePath()
    {
        std::wstring buffer(32768, L'\0');
        const DWORD length = GetModuleFileNameW(
            g_module,
            buffer.data(),
            static_cast<DWORD>(buffer.size()));
        if (length == 0 || length >= buffer.size())
        {
            return {};
        }

        buffer.resize(length);
        return buffer;
    }

    std::wstring EscapeJson(std::wstring_view value)
    {
        std::wstring escaped;
        escaped.reserve(value.size() + 16);
        constexpr wchar_t hex[] = L"0123456789abcdef";

        for (const wchar_t character : value)
        {
            switch (character)
            {
            case L'"':
                escaped += L"\\\"";
                break;
            case L'\\':
                escaped += L"\\\\";
                break;
            case L'\b':
                escaped += L"\\b";
                break;
            case L'\f':
                escaped += L"\\f";
                break;
            case L'\n':
                escaped += L"\\n";
                break;
            case L'\r':
                escaped += L"\\r";
                break;
            case L'\t':
                escaped += L"\\t";
                break;
            default:
                if (character < 0x20)
                {
                    escaped += L"\\u";
                    escaped += hex[(character >> 12) & 0xf];
                    escaped += hex[(character >> 8) & 0xf];
                    escaped += hex[(character >> 4) & 0xf];
                    escaped += hex[character & 0xf];
                }
                else
                {
                    escaped += character;
                }
                break;
            }
        }

        return escaped;
    }

    std::string ToUtf8(std::wstring_view value)
    {
        if (value.empty())
        {
            return {};
        }

        const int required = WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            nullptr,
            0,
            nullptr,
            nullptr);
        if (required <= 0)
        {
            return {};
        }

        std::string result(static_cast<size_t>(required), '\0');
        const int converted = WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            required,
            nullptr,
            nullptr);
        return converted == required ? result : std::string{};
    }

    std::wstring FormatUtc(const SYSTEMTIME& time)
    {
        wchar_t buffer[32]{};
        const HRESULT result = StringCchPrintfW(
            buffer,
            ARRAYSIZE(buffer),
            L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
            time.wYear,
            time.wMonth,
            time.wDay,
            time.wHour,
            time.wMinute,
            time.wSecond,
            time.wMilliseconds);
        return SUCCEEDED(result) ? std::wstring(buffer) : std::wstring{};
    }

    bool AddMinutes(const SYSTEMTIME& value, unsigned int minutes, SYSTEMTIME& result)
    {
        FILETIME fileTime{};
        if (!SystemTimeToFileTime(&value, &fileTime))
        {
            return false;
        }

        ULARGE_INTEGER ticks{};
        ticks.LowPart = fileTime.dwLowDateTime;
        ticks.HighPart = fileTime.dwHighDateTime;
        ticks.QuadPart += static_cast<ULONGLONG>(minutes) * 60ULL * 10000000ULL;
        fileTime.dwLowDateTime = ticks.LowPart;
        fileTime.dwHighDateTime = ticks.HighPart;
        return FileTimeToSystemTime(&fileTime, &result) != FALSE;
    }

    HRESULT WriteRequestFile(
        std::wstring_view operation,
        const std::vector<std::wstring>& sources,
        std::wstring& finalPath)
    {
        if (sources.empty() || sources.size() > kMaximumSources)
        {
            return E_INVALIDARG;
        }

        PWSTR localAppData{};
        HRESULT result = SHGetKnownFolderPath(
            FOLDERID_LocalAppData,
            KF_FLAG_CREATE,
            nullptr,
            &localAppData);
        if (FAILED(result))
        {
            return result;
        }

        const std::filesystem::path requestDirectory =
            std::filesystem::path(localAppData) / L"CopyShell" / L"Requests";
        CoTaskMemFree(localAppData);

        std::error_code error;
        std::filesystem::create_directories(requestDirectory, error);
        if (error)
        {
            return HRESULT_FROM_WIN32(error.value());
        }

        GUID requestId{};
        result = CoCreateGuid(&requestId);
        if (FAILED(result))
        {
            return result;
        }

        wchar_t guidWithBraces[40]{};
        if (StringFromGUID2(requestId, guidWithBraces, ARRAYSIZE(guidWithBraces)) == 0)
        {
            return E_FAIL;
        }

        std::wstring requestIdText(guidWithBraces);
        requestIdText = requestIdText.substr(1, requestIdText.size() - 2);

        SYSTEMTIME createdAt{};
        SYSTEMTIME expiresAt{};
        GetSystemTime(&createdAt);
        if (!AddMinutes(createdAt, 10, expiresAt))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        std::wstring json =
            L"{\"version\":1,\"requestId\":\"" + requestIdText +
            L"\",\"createdAtUtc\":\"" + FormatUtc(createdAt) +
            L"\",\"expiresAtUtc\":\"" + FormatUtc(expiresAt) +
            L"\",\"operation\":\"" + EscapeJson(operation) +
            L"\",\"sources\":[";
        for (size_t index = 0; index < sources.size(); ++index)
        {
            if (index > 0)
            {
                json += L",";
            }
            json += L"\"" + EscapeJson(sources[index]) + L"\"";
        }
        json +=
            L"],\"invoker\":{\"name\":\"CopyShell.ShellExtension\","
            L"\"version\":\"0.1.0\"}}";

        const std::string utf8 = ToUtf8(json);
        if (utf8.empty() || utf8.size() > kMaximumRequestBytes)
        {
            return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
        }

        const std::filesystem::path temporaryPath =
            requestDirectory / (requestIdText + L".tmp");
        const std::filesystem::path requestPath =
            requestDirectory / (requestIdText + L".json");
        HANDLE file = CreateFileW(
            temporaryPath.c_str(),
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_NEW,
            FILE_ATTRIBUTE_TEMPORARY,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        DWORD bytesWritten{};
        const BOOL wrote = WriteFile(
            file,
            utf8.data(),
            static_cast<DWORD>(utf8.size()),
            &bytesWritten,
            nullptr);
        const DWORD writeError = wrote ? ERROR_SUCCESS : GetLastError();
        const BOOL flushed = wrote ? FlushFileBuffers(file) : FALSE;
        const DWORD flushError = flushed ? ERROR_SUCCESS : GetLastError();
        CloseHandle(file);

        if (!wrote || bytesWritten != utf8.size() || !flushed)
        {
            DeleteFileW(temporaryPath.c_str());
            const DWORD errorCode =
                writeError != ERROR_SUCCESS ? writeError :
                flushError != ERROR_SUCCESS ? flushError :
                ERROR_WRITE_FAULT;
            return HRESULT_FROM_WIN32(errorCode);
        }

        if (!MoveFileExW(
                temporaryPath.c_str(),
                requestPath.c_str(),
                MOVEFILE_WRITE_THROUGH))
        {
            const DWORD moveError = GetLastError();
            DeleteFileW(temporaryPath.c_str());
            return HRESULT_FROM_WIN32(moveError);
        }

        finalPath = requestPath.wstring();
        return S_OK;
    }

    HRESULT LaunchApplication(
        std::wstring_view operation,
        const std::vector<std::wstring>& sources)
    {
        std::wstring requestPath;
        HRESULT result = WriteRequestFile(operation, sources, requestPath);
        if (FAILED(result))
        {
            return result;
        }

        const std::filesystem::path extensionPath(GetModulePath());
        const std::filesystem::path applicationPath =
            extensionPath.parent_path() / L"CopyShell.App.exe";
        if (!std::filesystem::exists(applicationPath))
        {
            DeleteFileW(requestPath.c_str());
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        }

        const std::wstring parameters = L"--request \"" + requestPath + L"\"";
        const HINSTANCE launchResult = ShellExecuteW(
            nullptr,
            L"open",
            applicationPath.c_str(),
            parameters.c_str(),
            applicationPath.parent_path().c_str(),
            SW_SHOWNORMAL);
        if (reinterpret_cast<INT_PTR>(launchResult) <= 32)
        {
            DeleteFileW(requestPath.c_str());
            return HRESULT_FROM_WIN32(ERROR_OPEN_FAILED);
        }

        return S_OK;
    }

    HRESULT GetExplorerSources(
        IShellItemArray* items,
        std::vector<std::wstring>& sources)
    {
        sources.clear();
        if (items == nullptr)
        {
            return E_INVALIDARG;
        }

        DWORD count{};
        HRESULT result = items->GetCount(&count);
        if (FAILED(result))
        {
            return result;
        }
        if (count == 0 || count > kMaximumSources)
        {
            return E_INVALIDARG;
        }

        sources.reserve(count);
        for (DWORD index = 0; index < count; ++index)
        {
            IShellItem* item{};
            result = items->GetItemAt(index, &item);
            if (FAILED(result))
            {
                return result;
            }

            PWSTR path{};
            result = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
            item->Release();
            if (FAILED(result))
            {
                return result;
            }

            try
            {
                sources.emplace_back(path);
            }
            catch (...)
            {
                CoTaskMemFree(path);
                throw;
            }
            CoTaskMemFree(path);
        }

        return sources.empty() ? E_INVALIDARG : S_OK;
    }

    HRESULT GetExplorerCommandState(
        UINT commandIndex,
        IShellItemArray* items,
        EXPCMDSTATE* state)
    {
        if (state == nullptr)
        {
            return E_POINTER;
        }

        *state = ECS_DISABLED;
        if (items == nullptr)
        {
            return S_OK;
        }

        DWORD count{};
        HRESULT result = items->GetCount(&count);
        if (FAILED(result) || count == 0 || count > kMaximumSources)
        {
            return SUCCEEDED(result) ? S_OK : result;
        }

        if (commandIndex != 2)
        {
            *state = ECS_ENABLED;
            return S_OK;
        }

        if (count != 1)
        {
            return S_OK;
        }

        IShellItem* item{};
        result = items->GetItemAt(0, &item);
        if (FAILED(result))
        {
            return result;
        }

        SFGAOF attributes{};
        result = item->GetAttributes(SFGAO_FOLDER | SFGAO_FILESYSTEM, &attributes);
        item->Release();
        if (FAILED(result))
        {
            return result;
        }

        if ((attributes & (SFGAO_FOLDER | SFGAO_FILESYSTEM)) ==
            (SFGAO_FOLDER | SFGAO_FILESYSTEM))
        {
            *state = ECS_ENABLED;
        }

        return S_OK;
    }

    HRESULT SetRegistryString(
        HKEY root,
        const std::wstring& subkey,
        const wchar_t* valueName,
        const std::wstring& value)
    {
        HKEY key{};
        const LSTATUS created = RegCreateKeyExW(
            root,
            subkey.c_str(),
            0,
            nullptr,
            REG_OPTION_NON_VOLATILE,
            KEY_SET_VALUE,
            nullptr,
            &key,
            nullptr);
        if (created != ERROR_SUCCESS)
        {
            return HRESULT_FROM_WIN32(created);
        }

        const LSTATUS written = RegSetValueExW(
            key,
            valueName,
            0,
            REG_SZ,
            reinterpret_cast<const BYTE*>(value.c_str()),
            static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
        RegCloseKey(key);
        return HRESULT_FROM_WIN32(written);
    }

    void DeleteRegistryTree(HKEY root, const std::wstring& subkey)
    {
        const LSTATUS result = RegDeleteTreeW(root, subkey.c_str());
        if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND)
        {
            OutputDebugStringW((L"CopyShell: registry cleanup failed: " + subkey + L"\n").c_str());
        }
    }

    class StorageMediumGuard final
    {
    public:
        explicit StorageMediumGuard(STGMEDIUM& medium) noexcept
            : _medium(&medium)
        {
        }

        ~StorageMediumGuard()
        {
            ReleaseStgMedium(_medium);
        }

        StorageMediumGuard(const StorageMediumGuard&) = delete;
        StorageMediumGuard& operator=(const StorageMediumGuard&) = delete;

    private:
        STGMEDIUM* _medium;
    };

    class GlobalMemoryLock final
    {
    public:
        explicit GlobalMemoryLock(HGLOBAL memory) noexcept
            : _memory(memory),
              _value(GlobalLock(memory))
        {
        }

        ~GlobalMemoryLock()
        {
            if (_value != nullptr)
            {
                GlobalUnlock(_memory);
            }
        }

        GlobalMemoryLock(const GlobalMemoryLock&) = delete;
        GlobalMemoryLock& operator=(const GlobalMemoryLock&) = delete;

        void* Get() const noexcept
        {
            return _value;
        }

    private:
        HGLOBAL _memory;
        void* _value;
    };

    class ExplorerCommand final : public IExplorerCommand
    {
    public:
        explicit ExplorerCommand(UINT commandIndex)
            : _commandIndex(commandIndex)
        {
            ++g_objectCount;
        }

        ~ExplorerCommand()
        {
            --g_objectCount;
        }

        IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (IsEqualIID(interfaceId, IID_IUnknown) ||
                IsEqualIID(interfaceId, IID_IExplorerCommand))
            {
                *object = static_cast<IExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const long count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
        {
            if (title == nullptr || _commandIndex >= kCommandCount)
            {
                return E_INVALIDARG;
            }
            return SHStrDupW(kCommands[_commandIndex].label, title);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
        {
            if (icon == nullptr)
            {
                return E_POINTER;
            }
            *icon = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* toolTip) override
        {
            if (toolTip == nullptr || _commandIndex >= kCommandCount)
            {
                return E_INVALIDARG;
            }
            return SHStrDupW(kCommands[_commandIndex].help, toolTip);
        }

        IFACEMETHODIMP GetCanonicalName(GUID* canonicalName) override
        {
            if (canonicalName == nullptr || _commandIndex >= kCommandCount)
            {
                return E_INVALIDARG;
            }
            *canonicalName = kCommandIds[_commandIndex];
            return S_OK;
        }

        IFACEMETHODIMP GetState(
            IShellItemArray* items,
            BOOL,
            EXPCMDSTATE* state) override
        {
            return GetExplorerCommandState(_commandIndex, items, state);
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            if (_commandIndex >= kCommandCount)
            {
                return E_INVALIDARG;
            }

            try
            {
                std::vector<std::wstring> sources;
                const HRESULT result = GetExplorerSources(items, sources);
                if (FAILED(result))
                {
                    return result;
                }
                return LaunchApplication(
                    kCommands[_commandIndex].operation,
                    sources);
            }
            catch (const std::bad_alloc&)
            {
                return E_OUTOFMEMORY;
            }
            catch (...)
            {
                return E_FAIL;
            }
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

        IFACEMETHODIMP EnumSubCommands(
            IEnumExplorerCommand** commands) override
        {
            if (commands == nullptr)
            {
                return E_POINTER;
            }
            *commands = nullptr;
            return E_NOTIMPL;
        }

    private:
        long _referenceCount{1};
        UINT _commandIndex;
    };

    class ExplorerCommandEnumerator final : public IEnumExplorerCommand
    {
    public:
        explicit ExplorerCommandEnumerator(ULONG currentIndex = 0)
            : _currentIndex(currentIndex)
        {
            ++g_objectCount;
        }

        ~ExplorerCommandEnumerator()
        {
            --g_objectCount;
        }

        IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (IsEqualIID(interfaceId, IID_IUnknown) ||
                IsEqualIID(interfaceId, IID_IEnumExplorerCommand))
            {
                *object = static_cast<IEnumExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const long count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP Next(
            ULONG count,
            IExplorerCommand** commands,
            ULONG* fetched) override
        {
            if (commands == nullptr || (count > 1 && fetched == nullptr))
            {
                return E_POINTER;
            }

            ULONG created{};
            while (created < count && _currentIndex < kCommandCount)
            {
                auto* command = new (std::nothrow) ExplorerCommand(_currentIndex);
                if (command == nullptr)
                {
                    break;
                }
                commands[created] = command;
                ++created;
                ++_currentIndex;
            }

            if (fetched != nullptr)
            {
                *fetched = created;
            }
            return created == count ? S_OK : S_FALSE;
        }

        IFACEMETHODIMP Skip(ULONG count) override
        {
            const ULONG remaining =
                static_cast<ULONG>(kCommandCount) - _currentIndex;
            const ULONG skipped = (count < remaining) ? count : remaining;
            _currentIndex += skipped;
            return skipped == count ? S_OK : S_FALSE;
        }

        IFACEMETHODIMP Reset() override
        {
            _currentIndex = 0;
            return S_OK;
        }

        IFACEMETHODIMP Clone(IEnumExplorerCommand** commands) override
        {
            if (commands == nullptr)
            {
                return E_POINTER;
            }
            *commands = new (std::nothrow) ExplorerCommandEnumerator(_currentIndex);
            return *commands == nullptr ? E_OUTOFMEMORY : S_OK;
        }

    private:
        long _referenceCount{1};
        ULONG _currentIndex;
    };

    class ShellExtension final :
        public IShellExtInit,
        public IContextMenu,
        public IExplorerCommand
    {
    public:
        ShellExtension()
        {
            ++g_objectCount;
        }

        ~ShellExtension()
        {
            --g_objectCount;
        }

        IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (IsEqualIID(interfaceId, IID_IUnknown) ||
                IsEqualIID(interfaceId, IID_IShellExtInit))
            {
                *object = static_cast<IShellExtInit*>(this);
            }
            else if (IsEqualIID(interfaceId, IID_IContextMenu))
            {
                *object = static_cast<IContextMenu*>(this);
            }
            else if (IsEqualIID(interfaceId, IID_IExplorerCommand))
            {
                *object = static_cast<IExplorerCommand*>(this);
            }
            else
            {
                return E_NOINTERFACE;
            }

            AddRef();
            return S_OK;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const long count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP Initialize(
            PCIDLIST_ABSOLUTE,
            IDataObject* dataObject,
            HKEY) override
        {
            try
            {
                _sources.clear();
                if (dataObject == nullptr)
                {
                    return E_INVALIDARG;
                }

                FORMATETC format{
                    static_cast<CLIPFORMAT>(CF_HDROP),
                    nullptr,
                    DVASPECT_CONTENT,
                    -1,
                    TYMED_HGLOBAL};
                STGMEDIUM medium{};
                HRESULT result = dataObject->GetData(&format, &medium);
                if (FAILED(result))
                {
                    return result;
                }

                const StorageMediumGuard mediumGuard(medium);
                const GlobalMemoryLock memoryLock(medium.hGlobal);
                const HDROP drop = static_cast<HDROP>(memoryLock.Get());
                if (drop == nullptr)
                {
                    return E_FAIL;
                }

                const UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
                if (count == 0 || count > kMaximumSources)
                {
                    return E_INVALIDARG;
                }

                _sources.reserve(count);
                for (UINT index = 0; index < count; ++index)
                {
                    const UINT length = DragQueryFileW(drop, index, nullptr, 0);
                    std::wstring path(static_cast<size_t>(length) + 1, L'\0');
                    if (DragQueryFileW(drop, index, path.data(), length + 1) != 0)
                    {
                        path.resize(length);
                        _sources.push_back(std::move(path));
                    }
                }

                return _sources.empty() ? E_INVALIDARG : S_OK;
            }
            catch (const std::bad_alloc&)
            {
                return E_OUTOFMEMORY;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP QueryContextMenu(
            HMENU menu,
            UINT indexMenu,
            UINT commandIdFirst,
            UINT,
            UINT flags) override
        {
            if ((flags & CMF_DEFAULTONLY) != 0 || _sources.empty())
            {
                return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
            }

            HMENU submenu = CreatePopupMenu();
            if (submenu == nullptr)
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }

            for (UINT index = 0; index < kCommandCount; ++index)
            {
                if (!AppendMenuW(
                        submenu,
                        MF_STRING,
                        static_cast<UINT_PTR>(commandIdFirst + index),
                        kCommands[index].label))
                {
                    DestroyMenu(submenu);
                    return HRESULT_FROM_WIN32(GetLastError());
                }
            }

            if (!InsertMenuW(
                    menu,
                    indexMenu,
                    MF_BYPOSITION | MF_POPUP,
                    reinterpret_cast<UINT_PTR>(submenu),
                    kDisplayName))
            {
                DestroyMenu(submenu);
                return HRESULT_FROM_WIN32(GetLastError());
            }

            return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, kCommandCount);
        }

        IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* commandInfo) override
        {
            if (commandInfo == nullptr)
            {
                return E_POINTER;
            }

            UINT commandIndex = UINT_MAX;
            if (HIWORD(commandInfo->lpVerb) == 0)
            {
                commandIndex = LOWORD(commandInfo->lpVerb);
            }
            else
            {
                for (UINT index = 0; index < kCommandCount; ++index)
                {
                    if (_stricmp(commandInfo->lpVerb, kCommands[index].verb) == 0)
                    {
                        commandIndex = index;
                        break;
                    }
                }
            }

            if (commandIndex >= kCommandCount)
            {
                return E_INVALIDARG;
            }

            try
            {
                return LaunchApplication(
                    kCommands[commandIndex].operation,
                    _sources);
            }
            catch (const std::bad_alloc&)
            {
                return E_OUTOFMEMORY;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        IFACEMETHODIMP GetCommandString(
            UINT_PTR commandId,
            UINT flags,
            UINT*,
            char* name,
            UINT nameLength) override
        {
            if (commandId >= kCommandCount || name == nullptr)
            {
                return E_INVALIDARG;
            }

            switch (flags)
            {
            case GCS_VERBA:
                return StringCchCopyA(name, nameLength, kCommands[commandId].verb);
            case GCS_HELPTEXTA:
            {
                const int converted = WideCharToMultiByte(
                    CP_ACP,
                    0,
                    kCommands[commandId].help,
                    -1,
                    name,
                    static_cast<int>(nameLength),
                    nullptr,
                    nullptr);
                return converted > 0 ? S_OK : HRESULT_FROM_WIN32(GetLastError());
            }
            case GCS_VERBW:
                return StringCchPrintfW(
                    reinterpret_cast<wchar_t*>(name),
                    nameLength,
                    L"%hs",
                    kCommands[commandId].verb);
            case GCS_HELPTEXTW:
                return StringCchCopyW(
                    reinterpret_cast<wchar_t*>(name),
                    nameLength,
                    kCommands[commandId].help);
            default:
                return E_NOTIMPL;
            }
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
        {
            if (title == nullptr)
            {
                return E_POINTER;
            }
            return SHStrDupW(kDisplayName, title);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
        {
            if (icon == nullptr)
            {
                return E_POINTER;
            }
            *icon = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* toolTip) override
        {
            if (toolTip == nullptr)
            {
                return E_POINTER;
            }
            return SHStrDupW(L"使用 CopyShell 执行可靠复制、移动或同步", toolTip);
        }

        IFACEMETHODIMP GetCanonicalName(GUID* canonicalName) override
        {
            if (canonicalName == nullptr)
            {
                return E_POINTER;
            }
            *canonicalName = kClassId;
            return S_OK;
        }

        IFACEMETHODIMP GetState(
            IShellItemArray* items,
            BOOL,
            EXPCMDSTATE* state) override
        {
            if (state == nullptr)
            {
                return E_POINTER;
            }

            *state = ECS_DISABLED;
            if (items == nullptr)
            {
                return S_OK;
            }

            DWORD count{};
            const HRESULT result = items->GetCount(&count);
            if (FAILED(result))
            {
                return result;
            }
            if (count > 0 && count <= kMaximumSources)
            {
                *state = ECS_ENABLED;
            }
            return S_OK;
        }

        IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override
        {
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            if (flags == nullptr)
            {
                return E_POINTER;
            }
            *flags = ECF_HASSUBCOMMANDS;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(
            IEnumExplorerCommand** commands) override
        {
            if (commands == nullptr)
            {
                return E_POINTER;
            }

            *commands = new (std::nothrow) ExplorerCommandEnumerator();
            return *commands == nullptr ? E_OUTOFMEMORY : S_OK;
        }

    private:
        long _referenceCount{1};
        std::vector<std::wstring> _sources;
    };

    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory()
        {
            ++g_objectCount;
        }

        ~ClassFactory()
        {
            --g_objectCount;
        }

        IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (IsEqualIID(interfaceId, IID_IUnknown) ||
                IsEqualIID(interfaceId, IID_IClassFactory))
            {
                *object = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const long count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP CreateInstance(
            IUnknown* outer,
            REFIID interfaceId,
            void** object) override
        {
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto* extension = new (std::nothrow) ShellExtension();
            if (extension == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            const HRESULT result = extension->QueryInterface(interfaceId, object);
            extension->Release();
            return result;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
            {
                ++g_objectCount;
            }
            else
            {
                --g_objectCount;
            }
            return S_OK;
        }

    private:
        long _referenceCount{1};
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
    return g_objectCount.load() == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(
    REFCLSID classId,
    REFIID interfaceId,
    void** object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    *object = nullptr;
    if (!IsEqualCLSID(classId, kClassId))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) ClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const HRESULT result = factory->QueryInterface(interfaceId, object);
    factory->Release();
    return result;
}

STDAPI DllRegisterServer()
{
    const std::wstring modulePath = GetModulePath();
    if (modulePath.empty())
    {
        return E_FAIL;
    }

    const std::wstring classRoot =
        std::wstring(L"Software\\Classes\\CLSID\\") + kClassIdText;
    HRESULT result = SetRegistryString(
        HKEY_CURRENT_USER,
        classRoot,
        nullptr,
        kDisplayName);
    if (FAILED(result))
    {
        return result;
    }

    result = SetRegistryString(
        HKEY_CURRENT_USER,
        classRoot + L"\\InprocServer32",
        nullptr,
        modulePath);
    if (FAILED(result))
    {
        return result;
    }

    result = SetRegistryString(
        HKEY_CURRENT_USER,
        classRoot + L"\\InprocServer32",
        L"ThreadingModel",
        L"Apartment");
    if (FAILED(result))
    {
        return result;
    }

    constexpr std::array<const wchar_t*, 2> handlers{{
        L"Software\\Classes\\*\\shellex\\ContextMenuHandlers\\CopyShell",
        L"Software\\Classes\\Directory\\shellex\\ContextMenuHandlers\\CopyShell",
    }};
    for (const wchar_t* handler : handlers)
    {
        result = SetRegistryString(
            HKEY_CURRENT_USER,
            handler,
            nullptr,
            kClassIdText);
        if (FAILED(result))
        {
            return result;
        }
    }

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    DeleteRegistryTree(
        HKEY_CURRENT_USER,
        L"Software\\Classes\\*\\shellex\\ContextMenuHandlers\\CopyShell");
    DeleteRegistryTree(
        HKEY_CURRENT_USER,
        L"Software\\Classes\\Directory\\shellex\\ContextMenuHandlers\\CopyShell");
    DeleteRegistryTree(
        HKEY_CURRENT_USER,
        std::wstring(L"Software\\Classes\\CLSID\\") + kClassIdText);

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

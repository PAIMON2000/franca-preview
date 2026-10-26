// FrancaLoader: experimental x64 Unity IL2CPP bootstrap and WinHTTP proxy.
// Bootstrap sequence adapted from UnityDoorstop (NeighTools/UnityDoorstop),
// src/bootstrap.c, il2cpp_doorstop_bootstrap; LGPL-2.1. See LICENSES/UnityDoorstop.txt.
// Franca modifications: bounded Unicode paths, explicit opt-in, staged diagnostics,
// lazy System32 forwarding, process-lifetime module pin and isolated native probes.
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>
#include <wchar.h>

#define PATH_CAP 32768
#define LOADER_VERSION "0.1.0-experimental"
typedef int (WINAPI *initialize_fn)(const char*, const char*, int, const char**, const char**, void**, unsigned int*);
typedef int (WINAPI *delegate_fn)(void*, unsigned int, const char*, const char*, const char*, void**);
typedef void (WINAPI *entrypoint_fn)(void);
extern const char* const canary_names[];
extern const unsigned int canary_count;
static INIT_ONCE real_once = INIT_ONCE_STATIC_INIT;
static HMODULE real_module;
static HANDLE worker_handle;
static volatile LONG status = 0;
static wchar_t game_dir[PATH_CAP], exe_path[PATH_CAP], target_path[PATH_CAP];
static wchar_t clr_path[PATH_CAP], corlib_dir[PATH_CAP], log_path[PATH_CAP];

static BOOL CALLBACK load_real(PINIT_ONCE once, PVOID param, PVOID* context)
{
    (void)once; (void)param; (void)context;
    wchar_t system_path[PATH_CAP];
    UINT length = GetSystemDirectoryW(system_path, PATH_CAP);
    if (!length || length >= PATH_CAP - 13 || wcscat_s(system_path, PATH_CAP, L"\\winhttp.dll")) return FALSE;
    // A bare basename can resolve to this proxy even with SYSTEM32 search flags.
    real_module = LoadLibraryExW(system_path, NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    return real_module != NULL;
}
static ULONG_PTR missing_api(void) { SetLastError(ERROR_PROC_NOT_FOUND); return 0; }
void* canary_resolve(unsigned int index)
{
    DWORD previous = GetLastError();
    FARPROC result = NULL;
    if (index < canary_count && InitOnceExecuteOnce(&real_once, load_real, NULL, NULL))
        result = GetProcAddress(real_module, canary_names[index]);
    SetLastError(previous);
    return result ? (void*)result : (void*)missing_api;
}
static void log_step(const char* format, ...)
{
    FILE* file = NULL;
    if (_wfopen_s(&file, log_path, L"ab") || !file) return;
    SYSTEMTIME now; GetLocalTime(&now);
    fprintf(file, "[%04u-%02u-%02u %02u:%02u:%02u.%03u] ", now.wYear, now.wMonth, now.wDay,
        now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
    va_list args; va_start(args, format); vfprintf(file, format, args); va_end(args);
    fputc('\n', file); fclose(file);
}
static DWORD fail(LONG code, const char* step, DWORD error)
{
    log_step("FAILED stage=%s code=%ld win32=%lu", step, code, error);
    InterlockedExchange(&status, code); return (DWORD)code;
}
static int join_path(wchar_t* output, const wchar_t* base, const wchar_t* relative)
{
    return _snwprintf_s(output, PATH_CAP, _TRUNCATE, L"%ls\\%ls", base, relative) >= 0;
}
static char* to_utf8(const wchar_t* value)
{
    int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, NULL, 0, NULL, NULL);
    if (!length) return NULL;
    char* result = (char*)malloc((size_t)length);
    if (result && !WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, result, length, NULL, NULL))
    { free(result); return NULL; }
    return result;
}
static int config_path(const wchar_t* ini, const wchar_t* section, const wchar_t* key,
    const wchar_t* fallback, wchar_t* output)
{
    wchar_t* value = (wchar_t*)calloc(PATH_CAP, sizeof(wchar_t));
    if (!value) return 0;
    DWORD count = GetPrivateProfileStringW(section, key, fallback, value, PATH_CAP, ini);
    int ok = count > 0 && count < PATH_CAP - 1;
    if (ok)
    {
        if ((count > 1 && value[1] == L':') || value[0] == L'\\' || value[0] == L'/')
            ok = wcscpy_s(output, PATH_CAP, value) == 0;
        else ok = join_path(output, game_dir, value);
    }
    free(value); return ok;
}
static int config_bool(const wchar_t* ini, const wchar_t* section, const wchar_t* key, const wchar_t* fallback)
{
    wchar_t value[16];
    GetPrivateProfileStringW(section, key, fallback, value, 16, ini);
    return _wcsicmp(value, L"true") == 0 || wcscmp(value, L"1") == 0;
}
static DWORD WINAPI loader_thread(LPVOID parameter)
{
    (void)parameter;
    DWORD length = GetModuleFileNameW(NULL, exe_path, PATH_CAP);
    if (!length || length >= PATH_CAP) return fail(10, "process-path", GetLastError());
    wcscpy_s(game_dir, PATH_CAP, exe_path);
    wchar_t* slash = wcsrchr(game_dir, L'\\');
    if (!slash) return fail(10, "process-directory", ERROR_BAD_PATHNAME);
    *slash = 0;
    wchar_t* ini = (wchar_t*)calloc(PATH_CAP, sizeof(wchar_t));
    wchar_t* folder = (wchar_t*)calloc(PATH_CAP, sizeof(wchar_t));
    if (!ini || !folder) { free(ini); free(folder); return fail(10, "allocation", ERROR_NOT_ENOUGH_MEMORY); }
    if (!join_path(folder, game_dir, L"BepInEx") ||
        !join_path(log_path, game_dir, L"BepInEx\\franca-loader.log") ||
        !join_path(ini, game_dir, L"doorstop_config.ini"))
    { free(ini); free(folder); return fail(10, "path-capacity", ERROR_BUFFER_OVERFLOW); }
    CreateDirectoryW(folder, NULL);
    free(folder);
    log_step("FrancaLoader version=%s architecture=x64", LOADER_VERSION);
    if (!InitOnceExecuteOnce(&real_once, load_real, NULL, NULL))
    { free(ini); return fail(11, "system-winhttp", GetLastError()); }
    log_step("PASS stage=system-winhttp");
    if (GetFileAttributesW(ini) == INVALID_FILE_ATTRIBUTES)
    { free(ini); return fail(12, "config-missing", ERROR_FILE_NOT_FOUND); }
    if (!config_bool(ini, L"General", L"enabled", L"true"))
    { free(ini); log_step("SKIP stage=config-disabled"); InterlockedExchange(&status, 2); return 0; }
    if (!config_bool(ini, L"Franca", L"experimental_bootstrap", L"false"))
    { free(ini); log_step("SKIP stage=experimental-opt-in-required"); InterlockedExchange(&status, 2); return 0; }
    wchar_t environment[16];
    if (!config_bool(ini, L"General", L"ignore_disable_switch", L"false") &&
        (GetEnvironmentVariableW(L"DOORSTOP_DISABLE", environment, 16) ||
         GetEnvironmentVariableW(L"DOORSTOP_INITIALIZED", environment, 16)))
    { free(ini); log_step("SKIP stage=inherited-doorstop-environment"); InterlockedExchange(&status, 2); return 0; }
    int configured = config_path(ini, L"General", L"target_assembly", L"", target_path) &&
        config_path(ini, L"Il2Cpp", L"coreclr_path", L"dotnet\\coreclr.dll", clr_path) &&
        config_path(ini, L"Il2Cpp", L"corlib_dir", L"dotnet", corlib_dir);
    free(ini);
    if (!configured) return fail(13, "config-paths", ERROR_INVALID_DATA);
    if (GetFileAttributesW(target_path) == INVALID_FILE_ATTRIBUTES)
        return fail(14, "target-assembly-missing", GetLastError());
    DWORD attributes = GetFileAttributesW(corlib_dir);
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY))
        return fail(15, "corlib-directory-missing", ERROR_PATH_NOT_FOUND);
    HMODULE clr = LoadLibraryExW(clr_path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!clr) return fail(16, "coreclr-load", GetLastError());
    initialize_fn initialize = (initialize_fn)GetProcAddress(clr, "coreclr_initialize");
    delegate_fn create_delegate = (delegate_fn)GetProcAddress(clr, "coreclr_create_delegate");
    if (!initialize || !create_delegate) return fail(17, "coreclr-exports", ERROR_PROC_NOT_FOUND);
    log_step("PASS stage=coreclr-exports");
    wchar_t* directory = _wcsdup(target_path);
    if (!directory) return fail(10, "allocation", ERROR_NOT_ENOUGH_MEMORY);
    slash = wcsrchr(directory, L'\\');
    if (!slash) { free(directory); return fail(13, "target-path", ERROR_BAD_PATHNAME); }
    *slash = 0;
    char* assembly = to_utf8(slash + 1);
    if (assembly) { char* dot = strrchr(assembly, '.'); if (dot) *dot = 0; }
    size_t app_length = wcslen(corlib_dir) + wcslen(directory) + 2;
    wchar_t* paths = (wchar_t*)calloc(app_length, sizeof(wchar_t));
    if (!paths || !assembly) { free(directory); free(paths); free(assembly); return fail(10, "allocation", ERROR_NOT_ENOUGH_MEMORY); }
    swprintf_s(paths, app_length, L"%ls;%ls", corlib_dir, directory);
    free(directory);
    char* app_paths = to_utf8(paths);
    char* process = to_utf8(exe_path);
    if (!app_paths || !process) { free(paths); free(assembly); free(app_paths); free(process); return fail(13, "utf8-paths", ERROR_NO_UNICODE_TRANSLATION); }
    if (!SetCurrentDirectoryW(game_dir)) { free(paths); free(assembly); free(app_paths); free(process); return fail(18, "working-directory", GetLastError()); }
    SetEnvironmentVariableW(L"DOORSTOP_INVOKE_DLL_PATH", target_path);
    SetEnvironmentVariableW(L"DOORSTOP_MANAGED_FOLDER_DIR", corlib_dir);
    SetEnvironmentVariableW(L"DOORSTOP_PROCESS_PATH", exe_path);
    SetEnvironmentVariableW(L"DOORSTOP_DLL_SEARCH_DIRS", paths);
    free(paths);
    const char* keys[] = { "APP_PATHS" };
    const char* values[] = { app_paths };
    void* host = NULL; unsigned int domain = 0;
    int result = initialize(process, "FrancaLoader Domain", 1, keys, values, &host, &domain);
    free(app_paths); free(process);
    if (result < 0) { free(assembly); log_step("HRESULT stage=coreclr-initialize value=0x%08x", result); return fail(19, "coreclr-initialize", ERROR_DLL_INIT_FAILED); }
    log_step("PASS stage=coreclr-initialize");
    entrypoint_fn startup = NULL;
    result = create_delegate(host, domain, assembly, "Doorstop.Entrypoint", "Start", (void**)&startup);
    free(assembly);
    if (result < 0 || !startup) { log_step("HRESULT stage=create-delegate value=0x%08x", result); return fail(20, "create-delegate", ERROR_PROC_NOT_FOUND); }
    log_step("PASS stage=create-delegate");
    SetEnvironmentVariableW(L"DOORSTOP_INITIALIZED", L"TRUE");
    log_step("BEGIN stage=managed-entrypoint");
    __try { startup(); }
    __except(EXCEPTION_EXECUTE_HANDLER) { return fail(21, "managed-entrypoint-exception", GetExceptionCode()); }
    log_step("PASS stage=managed-entrypoint-returned");
    InterlockedExchange(&status, 1); return 0;
}
DWORD WINAPI FrancaLoaderWait(DWORD milliseconds)
{
    if (!worker_handle) return 22;
    DWORD wait = WaitForSingleObject(worker_handle, milliseconds);
    return wait == WAIT_OBJECT_0 ? (DWORD)InterlockedCompareExchange(&status, 0, 0) : 23;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        // No LoadLibrary, CRT I/O, managed code or waiting under loader lock.
        // A proxy lives for the process lifetime; pin before starting deferred work.
        HMODULE pinned = NULL;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            (LPCWSTR)instance, &pinned)) return FALSE;
        worker_handle = CreateThread(NULL, 0, loader_thread, NULL, 0, NULL);
        if (!worker_handle) InterlockedExchange(&status, 22);
    }
    return TRUE;
}

// Franca native probe host. Uses only temporary fixtures and local WinHTTP handles; no requests.
#include <windows.h>
#include <winhttp.h>
#include <stdio.h>
#include <stdlib.h>
typedef DWORD (WINAPI *wait_fn)(DWORD);
typedef HINTERNET (WINAPI *open_fn)(LPCWSTR,DWORD,LPCWSTR,LPCWSTR,DWORD);
typedef HINTERNET (WINAPI *connect_fn)(HINTERNET,LPCWSTR,INTERNET_PORT,DWORD);
typedef HINTERNET (WINAPI *request_fn)(HINTERNET,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR*,DWORD);
typedef BOOL (WINAPI *close_fn)(HINTERNET);
int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 90;
    wchar_t dll_path[32768];
    if (!GetFullPathNameW(L".\\winhttp.dll",32768,dll_path,NULL)) return 91;
    HMODULE proxy = LoadLibraryExW(dll_path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!proxy) { printf("LOAD_FAILED=%lu\n",GetLastError()); return 91; }
    wait_fn wait = (wait_fn)GetProcAddress(proxy,"FrancaLoaderWait");
    open_fn open_session = (open_fn)GetProcAddress(proxy,"WinHttpOpen");
    connect_fn connect_local = (connect_fn)GetProcAddress(proxy,"WinHttpConnect");
    request_fn open_request = (request_fn)GetProcAddress(proxy,"WinHttpOpenRequest");
    close_fn close_handle = (close_fn)GetProcAddress(proxy,"WinHttpCloseHandle");
    if (!wait || !open_session || !connect_local || !open_request || !close_handle) return 92;
    DWORD64 image_base = 0;
    int unwind_ok = RtlLookupFunctionEntry((DWORD64)(ULONG_PTR)open_session,&image_base,NULL) != NULL;
    printf("UNWIND=%s\n",unwind_ok ? "PASS" : "FAIL");
    HINTERNET session = open_session(L"FrancaLoader probe",WINHTTP_ACCESS_TYPE_NO_PROXY,NULL,NULL,0);
    HINTERNET connection = session ? connect_local(session,L"localhost",8081,0) : NULL;
    HINTERNET request = connection ? open_request(connection,L"GET",L"/",NULL,NULL,NULL,0) : NULL;
    int ok = unwind_ok && session && connection && request;
    if (request) ok &= close_handle(request);
    if (connection) ok &= close_handle(connection);
    if (session) ok &= close_handle(session);
    printf("FORWARDING=%s\n",ok ? "PASS" : "FAIL");
    DWORD result = wait(10000);
    printf("LOADER_STATUS=%lu\n",result);
    // The proxy is process-pinned; never FreeLibrary during deferred work.
    return ok && result == (DWORD)_wtoi(argv[1]) ? 0 : 93;
}

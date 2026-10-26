/*
 * Franca thcrap stub launcher (franca-thcrap-stub.exe)
 *
 * Placed in the game directory under the original executable name (e.g. th11.exe)
 * while the real executable sits next to it as <name>.exe.frorig. On launch it
 * reads <own base name>.thcrapcmd (UTF-8, three lines: thcrap_loader.exe path,
 * runconfig js path, real executable path or a games.js game ID) and starts the thcrap loader with
 * "<loader>" "<config>" "<real>", waiting for the game and forwarding its exit
 * code. If the command file or any referenced file is missing it fails open:
 * the original executable (<own path>.frorig) is started directly so the game
 * always launches. Only when both paths fail a message box is shown.
 *
 * The marker string FRANCA_THCRAP_STUB lets TouhouThcrapService identify the
 * stub inside a game directory. Build: build-stub.ps1 (MSVC, x86, /MT).
 */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

#define CMD_MAX_BYTES 65535

/* Narrow marker for TouhouThcrapService health checks; volatile forces emission. */
static volatile const char kMarker[] = "FRANCA_THCRAP_STUB";

static DWORD LaunchAndWait(LPCWSTR application, LPWSTR commandLine)
{
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    DWORD code = 1;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));
    if (!CreateProcessW(application, commandLine, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi))
        return (DWORD)-1;
    WaitForSingleObject(pi.hProcess, INFINITE);
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return code;
}

static int FileExists(LPCWSTR path)
{
    DWORD attr = GetFileAttributesW(path);
    return attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY);
}

/* Read a small UTF-8 text file and convert it to UTF-16. Returns NULL on failure. */
static WCHAR *ReadUtf8File(LPCWSTR path)
{
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE)
        return NULL;
    DWORD size = GetFileSize(file, NULL);
    if (size == INVALID_FILE_SIZE || size > CMD_MAX_BYTES)
    {
        CloseHandle(file);
        return NULL;
    }
    char *bytes = (char *)malloc(size + 1);
    if (!bytes)
    {
        CloseHandle(file);
        return NULL;
    }
    DWORD read = 0;
    BOOL ok = ReadFile(file, bytes, size, &read, NULL);
    CloseHandle(file);
    if (!ok)
    {
        free(bytes);
        return NULL;
    }
    bytes[read] = '\0';
    int wideLen = MultiByteToWideChar(CP_UTF8, 0, bytes, (int)read, NULL, 0);
    if (wideLen <= 0)
    {
        free(bytes);
        return NULL;
    }
    WCHAR *wide = (WCHAR *)malloc((wideLen + 1) * sizeof(WCHAR));
    if (!wide)
    {
        free(bytes);
        return NULL;
    }
    MultiByteToWideChar(CP_UTF8, 0, bytes, (int)read, wide, wideLen);
    wide[wideLen] = L'\0';
    free(bytes);
    return wide;
}

/* Extract the next non-empty, trimmed line. Advances *cursor. Returns NULL when done. */
static WCHAR *NextLine(WCHAR **cursor)
{
    WCHAR *p = *cursor;
    while (*p)
    {
        WCHAR *end = p;
        while (*end && *end != L'\r' && *end != L'\n')
            end++;
        WCHAR *lineEnd = end;
        while (*end == L'\r' || *end == L'\n')
            end++;
        while (lineEnd > p && (lineEnd[-1] == L' ' || lineEnd[-1] == L'\t'))
            lineEnd--;
        *lineEnd = L'\0';
        *cursor = end;
        /* Skip BOM and surrounding blanks at line start */
        while (*p == L' ' || *p == L'\t' || *p == 0xFEFF)
            p++;
        if (*p)
            return p;
        p = end;
    }
    return NULL;
}

/* Append the arguments of the current command line (everything after argv[0]). */
static void AppendOwnArguments(WCHAR *buffer, size_t capacity)
{
    LPCWSTR cmd = GetCommandLineW();
    if (!cmd)
        return;
    if (*cmd == L'"')
    {
        cmd++;
        while (*cmd && *cmd != L'"')
            cmd++;
        if (*cmd == L'"')
            cmd++;
    }
    else
    {
        while (*cmd && *cmd != L' ' && *cmd != L'\t')
            cmd++;
    }
    wcsncat(buffer, cmd, capacity - wcslen(buffer) - 1);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE prev, LPWSTR cmdLine, int show)
{
    (void)instance; (void)prev; (void)cmdLine; (void)show;
    if (kMarker[0] == '\0')
        return 1;

    WCHAR self[MAX_PATH];
    DWORD len = GetModuleFileNameW(NULL, self, MAX_PATH);
    if (len == 0 || len >= MAX_PATH)
        return 1;

    /* <base> = own path without extension; cmd = <base>.thcrapcmd; frorig = self + .frorig */
    WCHAR base[MAX_PATH];
    WCHAR cmdPath[MAX_PATH];
    WCHAR frorig[MAX_PATH];
    wcscpy(base, self);
    WCHAR *dot = wcsrchr(base, L'.');
    WCHAR *slash = wcsrchr(base, L'\\');
    if (dot && (!slash || dot > slash))
        *dot = L'\0';
    _snwprintf(cmdPath, MAX_PATH, L"%s.thcrapcmd", base);
    _snwprintf(frorig, MAX_PATH, L"%s.frorig", self);

    WCHAR *content = ReadUtf8File(cmdPath);
    if (content)
    {
        WCHAR *cursor = content;
        WCHAR *loader = NextLine(&cursor);
        WCHAR *config = NextLine(&cursor);
        WCHAR *real = NextLine(&cursor);
        /* real can also be a bare game ID resolved through games.js (the .frorig
           backup name has no .exe extension, which the loader would not accept
           as a direct exe argument) — pass those through without a file check. */
        int realIsId = real != NULL
            && wcschr(real, L'\\') == NULL && wcschr(real, L'/') == NULL && wcschr(real, L':') == NULL;
        if (loader && config && real
            && FileExists(loader) && FileExists(config) && (realIsId || FileExists(real)))
        {
            WCHAR *command = (WCHAR *)malloc(32768 * sizeof(WCHAR));
            if (command)
            {
                _snwprintf(command, 32768, L"\"%s\" \"%s\" \"%s\"", loader, config, real);
                DWORD code = LaunchAndWait(loader, command);
                free(command);
                if (code != (DWORD)-1)
                {
                    free(content);
                    return (int)code;
                }
            }
        }
        free(content);
    }

    /* Fail-open: run the original game directly so a broken stub never blocks launch. */
    if (FileExists(frorig))
    {
        WCHAR *command = (WCHAR *)malloc(32768 * sizeof(WCHAR));
        if (command)
        {
            _snwprintf(command, 32768, L"\"%s\"", frorig);
            AppendOwnArguments(command, 32768);
            DWORD code = LaunchAndWait(frorig, command);
            free(command);
            if (code != (DWORD)-1)
                return (int)code;
        }
    }

    MessageBoxW(NULL,
        L"Franca thcrap stub (FRANCA_THCRAP_STUB): neither the thcrap loader nor the original game executable could be started.",
        L"Franca", MB_ICONERROR | MB_OK);
    return 1;
}

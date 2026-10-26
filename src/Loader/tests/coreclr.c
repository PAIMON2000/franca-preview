// Deterministic CoreCLR ABI fixture; never loaded into an actual game.
#include <windows.h>
#include <stdio.h>
#include <string.h>
static void WINAPI start(void)
{
    wchar_t fault[32];
    GetEnvironmentVariableW(L"FRANCA_FIXTURE_FAULT",fault,32);
    if (wcscmp(fault,L"entry") == 0) RaiseException(0xE0421234,0,0,NULL);
    HANDLE file=CreateFileW(L"native-entrypoint.ok",GENERIC_WRITE,0,NULL,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);
    if(file != INVALID_HANDLE_VALUE) CloseHandle(file);
}
__declspec(dllexport) int WINAPI coreclr_initialize(const char* exe,const char* domain,int count,
    const char** keys,const char** values,void** host,unsigned int* id)
{
    (void)exe;(void)domain;
    if (count != 1 || strcmp(keys[0],"APP_PATHS") || !values[0]) return (int)0x80004005;
    wchar_t fault[32];GetEnvironmentVariableW(L"FRANCA_FIXTURE_FAULT",fault,32);
    if(wcscmp(fault,L"initialize")==0) return (int)0x80004005;
    *host=(void*)1; *id=1; return 0;
}
#ifndef FRANCA_MISSING_EXPORT
__declspec(dllexport) int WINAPI coreclr_create_delegate(void* host,unsigned int id,const char* assembly,
    const char* type,const char* method,void** target)
{
    (void)host;(void)id;(void)assembly;
    wchar_t fault[32];GetEnvironmentVariableW(L"FRANCA_FIXTURE_FAULT",fault,32);
    if(wcscmp(fault,L"delegate")==0 || strcmp(type,"Doorstop.Entrypoint") || strcmp(method,"Start")) return (int)0x80004005;
    *target=(void*)start; return 0;
}
#endif

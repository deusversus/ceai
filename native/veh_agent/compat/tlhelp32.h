/*
 * Minimal tlhelp32.h for TinyCC — declares only what veh_agent.c needs.
 * Functions live in kernel32.dll; this header provides prototypes.
 */
#ifndef _COMPAT_TLHELP32_H
#define _COMPAT_TLHELP32_H

#include <windows.h>

#define TH32CS_SNAPTHREAD 0x00000004

typedef struct tagTHREADENTRY32 {
    DWORD dwSize;
    DWORD cntUsage;
    DWORD th32ThreadID;
    DWORD th32OwnerProcessID;
    LONG  tpBasePri;
    LONG  tpDeltaPri;
    DWORD dwFlags;
} THREADENTRY32, *PTHREADENTRY32, *LPTHREADENTRY32;

#ifdef __cplusplus
extern "C" {
#endif

HANDLE WINAPI CreateToolhelp32Snapshot(DWORD dwFlags, DWORD th32ProcessID);
BOOL   WINAPI Thread32First(HANDLE hSnapshot, LPTHREADENTRY32 lpte);
BOOL   WINAPI Thread32Next(HANDLE hSnapshot, LPTHREADENTRY32 lpte);

#ifdef __cplusplus
}
#endif

#endif /* _COMPAT_TLHELP32_H */

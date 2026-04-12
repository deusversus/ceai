/*
 * veh_agent.dll — Vectored Exception Handler agent for CE AI Suite
 *
 * Injected into the target process via LoadLibrary + CreateRemoteThread.
 * Installs a VEH that intercepts hardware breakpoint exceptions
 * (EXCEPTION_SINGLE_STEP) and reports hits via shared memory IPC.
 *
 * Compile: cl.exe /LD /O2 /W4 veh_agent.c /link /DLL /OUT:veh_agent.dll
 *
 * Shared memory name: Local\CEAISuite_VEH_{pid}
 * Command event:      Local\CEAISuite_VEH_Cmd_{pid}
 * Hit event:          Local\CEAISuite_VEH_Hit_{pid}
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* ── Shared memory layout ── */

#define SHM_MAGIC           0xCEAE
#define SHM_VERSION         1
#define SHM_HEADER_SIZE     0x40
#define HIT_ENTRY_SIZE      128
#define MAX_HITS            256
#define SHM_TOTAL_SIZE      (SHM_HEADER_SIZE + MAX_HITS * HIT_ENTRY_SIZE)

/* Commands (host → agent) */
#define CMD_IDLE            0
#define CMD_SET_BP          1
#define CMD_REMOVE_BP       2
#define CMD_SHUTDOWN        3

/* Agent status */
#define STATUS_LOADING      0
#define STATUS_READY        1
#define STATUS_ERROR        2
#define STATUS_SHUTDOWN     3

/* BP types (matches VehBreakpointType enum) */
#define BP_EXECUTE          0
#define BP_WRITE            1
#define BP_READWRITE        2

#pragma pack(push, 1)
typedef struct {
    DWORD   magic;          /* 0x000 */
    DWORD   version;        /* 0x004 */
    LONG    commandSlot;    /* 0x008 — volatile, interlocked */
    LONG    commandResult;  /* 0x00C */
    ULONG64 commandArg0;   /* 0x010 — address */
    DWORD   commandArg1;    /* 0x018 — type */
    DWORD   commandArg2;    /* 0x01C — DR slot */
    LONG    hitWriteIndex;  /* 0x020 — agent writes */
    LONG    hitReadIndex;   /* 0x024 — host writes */
    LONG    hitCount;       /* 0x028 — total */
    LONG    agentStatus;    /* 0x02C */
    DWORD   maxHits;        /* 0x030 */
    BYTE    reserved[12];   /* 0x034 */
    /* Hit ring buffer starts at 0x040 */
} ShmHeader;

typedef struct {
    ULONG64 address;        /* 0x00 — RIP */
    DWORD   threadId;       /* 0x08 */
    DWORD   hitType;        /* 0x0C */
    ULONG64 dr6;            /* 0x10 */
    ULONG64 rax, rbx, rcx, rdx;  /* 0x18-0x38 */
    ULONG64 rsi, rdi, rsp, rbp;  /* 0x38-0x58 */
    ULONG64 r8, r9, r10, r11;    /* 0x58-0x78 */
    ULONG64 timestamp;     /* 0x78 */
} HitEntry;
#pragma pack(pop)

/* ── Globals ── */

static PVOID            g_vehHandle = NULL;
static ShmHeader*       g_shm = NULL;
static HANDLE           g_shmHandle = NULL;
static HANDLE           g_cmdEvent = NULL;
static HANDLE           g_hitEvent = NULL;
static HANDLE           g_cmdThread = NULL;
static volatile BOOL    g_running = TRUE;

/* ── Forward declarations ── */

static LONG NTAPI VehHandler(PEXCEPTION_POINTERS ep);
static DWORD WINAPI CommandThread(LPVOID param);
static BOOL SetHardwareBp(ULONG64 address, DWORD type, DWORD drSlot);
static BOOL RemoveHardwareBp(DWORD drSlot);
static void WriteHitEntry(PCONTEXT ctx, ULONG64 dr6);

/* ── DR7 helpers ── */

/* Enable a DR slot in DR7. type: 0=execute, 1=write, 2=readwrite (mapped to DR7 R/W bits). */
static ULONG64 EnableDr7Slot(ULONG64 dr7, DWORD slot, DWORD type)
{
    DWORD rw, len;
    switch (type) {
        case BP_EXECUTE:    rw = 0; len = 0; break; /* 00=execute, len=00 */
        case BP_WRITE:      rw = 1; len = 3; break; /* 01=write, len=11 (8 bytes) */
        case BP_READWRITE:  rw = 3; len = 3; break; /* 11=r/w, len=11 */
        default:            rw = 0; len = 0; break;
    }
    /* Local enable bit: bit (slot*2) */
    dr7 |= (1ULL << (slot * 2));
    /* R/W field: bits 16+slot*4 .. 17+slot*4 */
    DWORD rwShift = 16 + slot * 4;
    dr7 &= ~(3ULL << rwShift);
    dr7 |= ((ULONG64)rw << rwShift);
    /* LEN field: bits 18+slot*4 .. 19+slot*4 */
    DWORD lenShift = 18 + slot * 4;
    dr7 &= ~(3ULL << lenShift);
    dr7 |= ((ULONG64)len << lenShift);
    return dr7;
}

static ULONG64 DisableDr7Slot(ULONG64 dr7, DWORD slot)
{
    dr7 &= ~(1ULL << (slot * 2));            /* clear local enable */
    dr7 &= ~(0xFULL << (16 + slot * 4));     /* clear R/W + LEN */
    return dr7;
}

/* ── VEH Handler ── */

static LONG NTAPI VehHandler(PEXCEPTION_POINTERS ep)
{
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP)
        return EXCEPTION_CONTINUE_SEARCH;

    if (!g_shm || g_shm->agentStatus != STATUS_READY)
        return EXCEPTION_CONTINUE_SEARCH;

    PCONTEXT ctx = ep->ContextRecord;
    ULONG64 dr6 = ctx->Dr6;

    /* Check if any of DR0-DR3 triggered (bits 0-3 of DR6) */
    if ((dr6 & 0xF) == 0)
        return EXCEPTION_CONTINUE_SEARCH;

    WriteHitEntry(ctx, dr6);

    /* Clear DR6 to acknowledge the hit */
    ctx->Dr6 = 0;

    /* Signal the host that a hit occurred */
    if (g_hitEvent)
        SetEvent(g_hitEvent);

    return EXCEPTION_CONTINUE_EXECUTION;
}

/* ── Hit ring buffer write ── */

static void WriteHitEntry(PCONTEXT ctx, ULONG64 dr6)
{
    LONG idx = InterlockedIncrement(&g_shm->hitWriteIndex) - 1;
    LONG slot = idx % MAX_HITS;
    BYTE* base = (BYTE*)g_shm + SHM_HEADER_SIZE + slot * HIT_ENTRY_SIZE;
    HitEntry* hit = (HitEntry*)base;

    LARGE_INTEGER ts;
    QueryPerformanceCounter(&ts);

    hit->address  = ctx->Rip;
    hit->threadId = GetCurrentThreadId();
    hit->hitType  = 0; /* Determine from DR6 which slot fired */
    hit->dr6      = dr6;
    hit->rax = ctx->Rax; hit->rbx = ctx->Rbx;
    hit->rcx = ctx->Rcx; hit->rdx = ctx->Rdx;
    hit->rsi = ctx->Rsi; hit->rdi = ctx->Rdi;
    hit->rsp = ctx->Rsp; hit->rbp = ctx->Rbp;
    hit->r8  = ctx->R8;  hit->r9  = ctx->R9;
    hit->r10 = ctx->R10; hit->r11 = ctx->R11;
    hit->timestamp = (ULONG64)ts.QuadPart;

    InterlockedIncrement(&g_shm->hitCount);
}

/* ── Hardware breakpoint management ── */

static BOOL SetHardwareBp(ULONG64 address, DWORD type, DWORD drSlot)
{
    if (drSlot > 3) return FALSE;

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return FALSE;

    THREADENTRY32 te;
    te.dwSize = sizeof(te);
    DWORD pid = GetCurrentProcessId();
    DWORD myTid = GetCurrentThreadId();
    BOOL ok = TRUE;

    if (Thread32First(snap, &te)) {
        do {
            if (te.th32OwnerProcessID != pid) continue;
            if (te.th32ThreadID == myTid) continue; /* skip our command thread */

            HANDLE hThread = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME,
                                        FALSE, te.th32ThreadID);
            if (!hThread) continue;

            SuspendThread(hThread);

            CONTEXT ctx;
            ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            if (GetThreadContext(hThread, &ctx)) {
                /* Set DR[slot] to the target address */
                switch (drSlot) {
                    case 0: ctx.Dr0 = address; break;
                    case 1: ctx.Dr1 = address; break;
                    case 2: ctx.Dr2 = address; break;
                    case 3: ctx.Dr3 = address; break;
                }
                ctx.Dr7 = EnableDr7Slot(ctx.Dr7, drSlot, type);
                ctx.Dr6 = 0;  /* clear pending status */
                if (!SetThreadContext(hThread, &ctx))
                    ok = FALSE;
            } else {
                ok = FALSE;
            }

            ResumeThread(hThread);
            CloseHandle(hThread);
        } while (Thread32Next(snap, &te));
    }

    CloseHandle(snap);
    return ok;
}

static BOOL RemoveHardwareBp(DWORD drSlot)
{
    if (drSlot > 3) return FALSE;

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return FALSE;

    THREADENTRY32 te;
    te.dwSize = sizeof(te);
    DWORD pid = GetCurrentProcessId();
    DWORD myTid = GetCurrentThreadId();

    if (Thread32First(snap, &te)) {
        do {
            if (te.th32OwnerProcessID != pid) continue;
            if (te.th32ThreadID == myTid) continue;

            HANDLE hThread = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME,
                                        FALSE, te.th32ThreadID);
            if (!hThread) continue;

            SuspendThread(hThread);

            CONTEXT ctx;
            ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            if (GetThreadContext(hThread, &ctx)) {
                switch (drSlot) {
                    case 0: ctx.Dr0 = 0; break;
                    case 1: ctx.Dr1 = 0; break;
                    case 2: ctx.Dr2 = 0; break;
                    case 3: ctx.Dr3 = 0; break;
                }
                ctx.Dr7 = DisableDr7Slot(ctx.Dr7, drSlot);
                SetThreadContext(hThread, &ctx);
            }

            ResumeThread(hThread);
            CloseHandle(hThread);
        } while (Thread32Next(snap, &te));
    }

    CloseHandle(snap);
    return TRUE;
}

/* ── Command processing thread ── */

static DWORD WINAPI CommandThread(LPVOID param)
{
    (void)param;

    while (g_running) {
        /* Wait for command event or timeout (check shutdown flag) */
        WaitForSingleObject(g_cmdEvent, 100);

        LONG cmd = InterlockedExchange(&g_shm->commandSlot, CMD_IDLE);
        if (cmd == CMD_IDLE) continue;

        LONG result = 0;

        switch (cmd) {
            case CMD_SET_BP:
                result = SetHardwareBp(g_shm->commandArg0, g_shm->commandArg1, g_shm->commandArg2)
                         ? 0 : -1;
                break;

            case CMD_REMOVE_BP:
                result = RemoveHardwareBp(g_shm->commandArg2) ? 0 : -1;
                break;

            case CMD_SHUTDOWN:
                g_running = FALSE;
                result = 0;
                break;

            default:
                result = -1;
                break;
        }

        InterlockedExchange(&g_shm->commandResult, result);
    }

    return 0;
}

/* ── DLL entry point ── */

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)hModule;
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        char shmName[128], cmdName[128], hitName[128];
        DWORD pid = GetCurrentProcessId();

        wsprintfA(shmName, "Local\\CEAISuite_VEH_%u", pid);
        wsprintfA(cmdName, "Local\\CEAISuite_VEH_Cmd_%u", pid);
        wsprintfA(hitName, "Local\\CEAISuite_VEH_Hit_%u", pid);

        /* Open shared memory (created by host before injection) */
        g_shmHandle = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, shmName);
        if (!g_shmHandle) {
            return FALSE; /* Host didn't create shared memory — abort */
        }

        g_shm = (ShmHeader*)MapViewOfFile(g_shmHandle, FILE_MAP_ALL_ACCESS, 0, 0, SHM_TOTAL_SIZE);
        if (!g_shm) {
            CloseHandle(g_shmHandle);
            return FALSE;
        }

        /* Validate magic */
        if (g_shm->magic != SHM_MAGIC || g_shm->version != SHM_VERSION) {
            UnmapViewOfFile(g_shm);
            CloseHandle(g_shmHandle);
            g_shm = NULL;
            return FALSE;
        }

        /* Open events (created by host) */
        g_cmdEvent = OpenEventA(EVENT_ALL_ACCESS, FALSE, cmdName);
        g_hitEvent = OpenEventA(EVENT_ALL_ACCESS, FALSE, hitName);

        /* Install VEH — priority handler (called first) */
        g_vehHandle = AddVectoredExceptionHandler(1, VehHandler);
        if (!g_vehHandle) {
            InterlockedExchange(&g_shm->agentStatus, STATUS_ERROR);
            return FALSE;
        }

        /* Start command processing thread */
        g_running = TRUE;
        g_cmdThread = CreateThread(NULL, 0, CommandThread, NULL, 0, NULL);

        /* Signal ready */
        InterlockedExchange(&g_shm->agentStatus, STATUS_READY);
    }
    else if (reason == DLL_PROCESS_DETACH) {
        g_running = FALSE;

        if (g_vehHandle) {
            RemoveVectoredExceptionHandler(g_vehHandle);
            g_vehHandle = NULL;
        }

        /* Remove all hardware breakpoints */
        for (DWORD i = 0; i < 4; i++)
            RemoveHardwareBp(i);

        InterlockedExchange(&g_shm->agentStatus, STATUS_SHUTDOWN);

        if (g_cmdThread) {
            WaitForSingleObject(g_cmdThread, 1000);
            CloseHandle(g_cmdThread);
        }

        if (g_shm) { UnmapViewOfFile(g_shm); g_shm = NULL; }
        if (g_shmHandle) { CloseHandle(g_shmHandle); g_shmHandle = NULL; }
        if (g_cmdEvent) { CloseHandle(g_cmdEvent); g_cmdEvent = NULL; }
        if (g_hitEvent) { CloseHandle(g_hitEvent); g_hitEvent = NULL; }
    }

    return TRUE;
}

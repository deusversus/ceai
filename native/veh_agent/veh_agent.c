/*
 * veh_agent.dll — Vectored Exception Handler agent for CE AI Suite
 *
 * Injected into the target process via LoadLibrary + CreateRemoteThread.
 * Installs a VEH that intercepts hardware breakpoint exceptions
 * (EXCEPTION_SINGLE_STEP) and reports hits via shared memory IPC.
 *
 * Compile (x64): cl.exe /LD /O2 /W4 veh_agent.c /link /DLL /OUT:veh_agent.dll
 * Compile (x86): cl.exe /LD /O2 /W4 veh_agent.c /link /DLL /OUT:veh_agent_x86.dll
 *
 * Shared memory name: Local\CEAISuite_VEH_{pid}
 * Command event:      Local\CEAISuite_VEH_Cmd_{pid}
 * Hit event:          Local\CEAISuite_VEH_Hit_{pid}
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* ── Shared memory layout ── */

#define SHM_MAGIC           0xCEAE
#define SHM_VERSION         2
#define SHM_HEADER_SIZE     0x40
#define HIT_ENTRY_SIZE      128
#define DEFAULT_MAX_HITS    4096

/* Commands (host → agent) */
#define CMD_IDLE            0
#define CMD_SET_BP          1
#define CMD_REMOVE_BP       2
#define CMD_SHUTDOWN        3
#define CMD_REFRESH_THREADS 4

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
    LONG    overflowCount;  /* 0x034 — ring buffer overflows [V2] */
    DWORD   commandArg3;    /* 0x038 — data size (1/2/4/8) [V2] */
    LONG    heartbeat;      /* 0x03C — GetTickCount() [V2] */
    /* Hit ring buffer starts at 0x040 */
} ShmHeader;

typedef struct {
    ULONG64 address;        /* 0x00 — RIP/EIP */
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
static DWORD            g_maxHits = DEFAULT_MAX_HITS;

/* Per-slot tracking for hitType population and thread refresh */
static ULONG64          g_bpAddresses[4] = {0};
static DWORD            g_bpTypes[4] = {0};
static DWORD            g_bpSizes[4] = {0};
static volatile BOOL    g_bpActive[4] = {FALSE, FALSE, FALSE, FALSE};

/* ── Forward declarations ── */

static LONG NTAPI VehHandler(PEXCEPTION_POINTERS ep);
static DWORD WINAPI CommandThread(LPVOID param);
static BOOL SetHardwareBp(ULONG64 address, DWORD type, DWORD drSlot, DWORD dataSize);
static BOOL RemoveHardwareBp(DWORD drSlot);
static void RefreshThreadBreakpoints(void);
static void WriteHitEntry(PCONTEXT ctx, ULONG64 dr6);

/* ── DR7 helpers ── */

/* Enable a DR slot in DR7. type: 0=execute, 1=write, 2=readwrite. dataSize: 1/2/4/8. */
static ULONG64 EnableDr7Slot(ULONG64 dr7, DWORD slot, DWORD type, DWORD dataSize)
{
    DWORD rw, len;
    switch (type) {
        case BP_EXECUTE:    rw = 0; len = 0; break; /* 00=execute, len=00 (1 byte) */
        case BP_WRITE:      rw = 1; goto data_len;
        case BP_READWRITE:  rw = 3; goto data_len;
        default:            rw = 0; len = 0; break;
    }
    goto apply;

data_len:
    /* Intel DR7 LEN encoding: 00=1byte, 01=2bytes, 11=4bytes, 10=8bytes */
    switch (dataSize) {
        case 1:  len = 0; break;
        case 2:  len = 1; break;
        case 4:  len = 3; break;
        case 8:  len = 2; break;
        default: len = 2; break; /* default to 8 bytes */
    }

apply:
    /* Local enable bit: bit (slot*2) */
    dr7 |= (1ULL << (slot * 2));
    /* R/W field: bits 16+slot*4 .. 17+slot*4 */
    {
        DWORD rwShift = 16 + slot * 4;
        dr7 &= ~(3ULL << rwShift);
        dr7 |= ((ULONG64)rw << rwShift);
    }
    /* LEN field: bits 18+slot*4 .. 19+slot*4 */
    {
        DWORD lenShift = 18 + slot * 4;
        dr7 &= ~(3ULL << lenShift);
        dr7 |= ((ULONG64)len << lenShift);
    }
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
    LONG writeIdx, readIdx;
    LONG slot;

    /* Check for overflow before claiming a slot.
     * Use interlocked reads to ensure compiler doesn't reorder these
     * relative to the InterlockedIncrement below. */
    writeIdx = InterlockedCompareExchange(&g_shm->hitWriteIndex, 0, 0);
    readIdx = InterlockedCompareExchange(&g_shm->hitReadIndex, 0, 0);
    if ((DWORD)(writeIdx - readIdx) >= g_maxHits)
    {
        InterlockedIncrement(&g_shm->overflowCount);
        return; /* ring full — drop this hit rather than overwrite unread data */
    }

    writeIdx = InterlockedIncrement(&g_shm->hitWriteIndex) - 1;

    {
        slot = (LONG)((DWORD)writeIdx % g_maxHits);
        BYTE* base = (BYTE*)g_shm + SHM_HEADER_SIZE + slot * HIT_ENTRY_SIZE;
        HitEntry* hit = (HitEntry*)base;
        LARGE_INTEGER ts;
        DWORD triggeredSlot = 0;
        DWORD i;

        QueryPerformanceCounter(&ts);

        /* Determine which DR slot triggered from DR6 bits 0-3 */
        for (i = 0; i < 4; i++) {
            if (dr6 & (1ULL << i)) { triggeredSlot = i; break; }
        }

#ifdef _M_IX86
        hit->address  = (ULONG64)ctx->Eip;
        hit->threadId = GetCurrentThreadId();
        hit->hitType  = g_bpTypes[triggeredSlot];
        hit->dr6      = dr6;
        hit->rax = ctx->Eax; hit->rbx = ctx->Ebx;
        hit->rcx = ctx->Ecx; hit->rdx = ctx->Edx;
        hit->rsi = ctx->Esi; hit->rdi = ctx->Edi;
        hit->rsp = ctx->Esp; hit->rbp = ctx->Ebp;
        hit->r8  = 0;        hit->r9  = 0;
        hit->r10 = 0;        hit->r11 = 0;
#else
        hit->address  = ctx->Rip;
        hit->threadId = GetCurrentThreadId();
        hit->hitType  = g_bpTypes[triggeredSlot];
        hit->dr6      = dr6;
        hit->rax = ctx->Rax; hit->rbx = ctx->Rbx;
        hit->rcx = ctx->Rcx; hit->rdx = ctx->Rdx;
        hit->rsi = ctx->Rsi; hit->rdi = ctx->Rdi;
        hit->rsp = ctx->Rsp; hit->rbp = ctx->Rbp;
        hit->r8  = ctx->R8;  hit->r9  = ctx->R9;
        hit->r10 = ctx->R10; hit->r11 = ctx->R11;
#endif
        hit->timestamp = (ULONG64)ts.QuadPart;
    }

    InterlockedIncrement(&g_shm->hitCount);
}

/* ── Hardware breakpoint management ── */

static BOOL SetHardwareBp(ULONG64 address, DWORD type, DWORD drSlot, DWORD dataSize)
{
    HANDLE snap;
    THREADENTRY32 te;
    DWORD pid, myTid;
    BOOL ok = TRUE;

    if (drSlot > 3) return FALSE;
    if (dataSize == 0) dataSize = (type == BP_EXECUTE) ? 1 : 8;

    /* Record in tracking arrays for hitType population and thread refresh */
    g_bpAddresses[drSlot] = address;
    g_bpTypes[drSlot] = type;
    g_bpSizes[drSlot] = dataSize;
    InterlockedExchange((volatile LONG*)&g_bpActive[drSlot], TRUE);

    snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return FALSE;

    te.dwSize = sizeof(te);
    pid = GetCurrentProcessId();
    myTid = GetCurrentThreadId();

    if (Thread32First(snap, &te)) {
        do {
            HANDLE hThread;
            CONTEXT ctx;

            if (te.th32OwnerProcessID != pid) continue;
            if (te.th32ThreadID == myTid) continue; /* skip our command thread */

            hThread = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME,
                                        FALSE, te.th32ThreadID);
            if (!hThread) continue;

            SuspendThread(hThread);

            ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            if (GetThreadContext(hThread, &ctx)) {
                /* Set DR[slot] to the target address */
                switch (drSlot) {
                    case 0: ctx.Dr0 = address; break;
                    case 1: ctx.Dr1 = address; break;
                    case 2: ctx.Dr2 = address; break;
                    case 3: ctx.Dr3 = address; break;
                }
                ctx.Dr7 = EnableDr7Slot(ctx.Dr7, drSlot, type, dataSize);
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
    HANDLE snap;
    THREADENTRY32 te;
    DWORD pid, myTid;

    if (drSlot > 3) return FALSE;

    /* Clear tracking arrays */
    InterlockedExchange((volatile LONG*)&g_bpActive[drSlot], FALSE);
    g_bpAddresses[drSlot] = 0;
    g_bpTypes[drSlot] = 0;
    g_bpSizes[drSlot] = 0;

    snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return FALSE;

    te.dwSize = sizeof(te);
    pid = GetCurrentProcessId();
    myTid = GetCurrentThreadId();

    if (Thread32First(snap, &te)) {
        do {
            HANDLE hThread;
            CONTEXT ctx;

            if (te.th32OwnerProcessID != pid) continue;
            if (te.th32ThreadID == myTid) continue;

            hThread = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME,
                                        FALSE, te.th32ThreadID);
            if (!hThread) continue;

            SuspendThread(hThread);

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

/* ── Thread refresh: apply all active BPs to any threads missing them ── */

static void RefreshThreadBreakpoints(void)
{
    HANDLE snap;
    THREADENTRY32 te;
    DWORD pid, myTid;
    DWORD i;
    BOOL anyActive = FALSE;

    for (i = 0; i < 4; i++) {
        if (g_bpActive[i]) { anyActive = TRUE; break; }
    }
    if (!anyActive) return;

    snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return;

    te.dwSize = sizeof(te);
    pid = GetCurrentProcessId();
    myTid = GetCurrentThreadId();

    if (Thread32First(snap, &te)) {
        do {
            HANDLE hThread;
            CONTEXT ctx;

            if (te.th32OwnerProcessID != pid) continue;
            if (te.th32ThreadID == myTid) continue;

            hThread = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME,
                                 FALSE, te.th32ThreadID);
            if (!hThread) continue;

            SuspendThread(hThread);
            ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            if (GetThreadContext(hThread, &ctx)) {
                BOOL needsUpdate = FALSE;
                for (i = 0; i < 4; i++) {
                    ULONG64 currentDr;
                    if (!g_bpActive[i]) continue;

                    switch (i) {
                        case 0: currentDr = ctx.Dr0; break;
                        case 1: currentDr = ctx.Dr1; break;
                        case 2: currentDr = ctx.Dr2; break;
                        case 3: currentDr = ctx.Dr3; break;
                        default: currentDr = 0; break;
                    }
                    /* Check both DR address AND DR7 local enable bit.
                     * Anti-cheat may clear DR7 while leaving DR addresses intact. */
                    {
                        BOOL dr7Enabled = (ctx.Dr7 & (1ULL << (i * 2))) != 0;
                        if (currentDr != g_bpAddresses[i] || !dr7Enabled) {
                            switch (i) {
                                case 0: ctx.Dr0 = g_bpAddresses[i]; break;
                                case 1: ctx.Dr1 = g_bpAddresses[i]; break;
                                case 2: ctx.Dr2 = g_bpAddresses[i]; break;
                                case 3: ctx.Dr3 = g_bpAddresses[i]; break;
                            }
                            ctx.Dr7 = EnableDr7Slot(ctx.Dr7, i, g_bpTypes[i], g_bpSizes[i]);
                            needsUpdate = TRUE;
                        }
                    }
                }
                if (needsUpdate) {
                    ctx.Dr6 = 0;
                    SetThreadContext(hThread, &ctx);
                }
            }
            ResumeThread(hThread);
            CloseHandle(hThread);
        } while (Thread32Next(snap, &te));
    }

    CloseHandle(snap);
}

/* ── Command processing thread ── */

static DWORD WINAPI CommandThread(LPVOID param)
{
    DWORD refreshCounter = 0;

    (void)param;

    while (g_running) {
        LONG cmd;
        LONG result = 0;

        /* Wait for command event or timeout (check shutdown flag) */
        WaitForSingleObject(g_cmdEvent, 100);

        /* Update heartbeat */
        InterlockedExchange(&g_shm->heartbeat, (LONG)GetTickCount());

        cmd = InterlockedExchange(&g_shm->commandSlot, CMD_IDLE);
        if (cmd == CMD_IDLE) {
            /* Periodic thread refresh every ~500ms (5 iterations * 100ms) */
            refreshCounter++;
            if (refreshCounter >= 5) {
                refreshCounter = 0;
                RefreshThreadBreakpoints();
            }
            continue;
        }

        refreshCounter = 0; /* reset on command activity */

        switch (cmd) {
            case CMD_SET_BP:
                result = SetHardwareBp(g_shm->commandArg0, g_shm->commandArg1,
                                       g_shm->commandArg2, g_shm->commandArg3)
                         ? 0 : -1;
                break;

            case CMD_REMOVE_BP:
                result = RemoveHardwareBp(g_shm->commandArg2) ? 0 : -1;
                break;

            case CMD_REFRESH_THREADS:
                RefreshThreadBreakpoints();
                result = 0;
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

/* ── Agent initialization (runs on dedicated thread, NOT under loader lock) ── */

static DWORD WINAPI AgentInitThread(LPVOID param)
{
    char shmName[128], cmdName[128], hitName[128];
    DWORD pid = GetCurrentProcessId();
    DWORD shmTotalSize;

    (void)param;

    wsprintfA(shmName, "Local\\CEAISuite_VEH_%u", pid);
    wsprintfA(cmdName, "Local\\CEAISuite_VEH_Cmd_%u", pid);
    wsprintfA(hitName, "Local\\CEAISuite_VEH_Hit_%u", pid);

    /* Open shared memory (created by host before injection) */
    g_shmHandle = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, shmName);
    if (!g_shmHandle) {
        return 1;
    }

    /* First map just the header to read maxHits */
    g_shm = (ShmHeader*)MapViewOfFile(g_shmHandle, FILE_MAP_ALL_ACCESS, 0, 0, SHM_HEADER_SIZE);
    if (!g_shm) {
        CloseHandle(g_shmHandle);
        return 1;
    }

    /* Validate magic and version */
    if (g_shm->magic != SHM_MAGIC || g_shm->version != SHM_VERSION) {
        UnmapViewOfFile(g_shm);
        CloseHandle(g_shmHandle);
        g_shm = NULL;
        return 1;
    }

    /* Read host-configured maxHits */
    g_maxHits = g_shm->maxHits;
    if (g_maxHits == 0 || g_maxHits > 65536) g_maxHits = DEFAULT_MAX_HITS;
    shmTotalSize = SHM_HEADER_SIZE + g_maxHits * HIT_ENTRY_SIZE;

    /* Remap with full size including ring buffer */
    UnmapViewOfFile(g_shm);
    g_shm = (ShmHeader*)MapViewOfFile(g_shmHandle, FILE_MAP_ALL_ACCESS, 0, 0, shmTotalSize);
    if (!g_shm) {
        CloseHandle(g_shmHandle);
        return 1;
    }

    /* Open events (created by host) */
    g_cmdEvent = OpenEventA(EVENT_ALL_ACCESS, FALSE, cmdName);
    g_hitEvent = OpenEventA(EVENT_ALL_ACCESS, FALSE, hitName);

    /* Install VEH — priority handler (called first) */
    g_vehHandle = AddVectoredExceptionHandler(1, VehHandler);
    if (!g_vehHandle) {
        InterlockedExchange(&g_shm->agentStatus, STATUS_ERROR);
        return 1;
    }

    /* Signal ready — then fall through to command loop */
    InterlockedExchange(&g_shm->agentStatus, STATUS_READY);

    /* Run command loop on this thread (reuse init thread as command thread) */
    CommandThread(NULL);
    return 0;
}

/* ── DLL entry point ── */

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)hModule;
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        /* Minimize work under loader lock — only create the init thread.
         * All SHM mapping, event opening, and VEH registration happens on the
         * init thread, outside the loader lock, avoiding potential deadlocks. */
        g_running = TRUE;
        g_cmdThread = CreateThread(NULL, 0, AgentInitThread, NULL, 0, NULL);
        if (!g_cmdThread) return FALSE;
    }
    else if (reason == DLL_PROCESS_DETACH) {
        DWORD i;
        g_running = FALSE;

        if (g_vehHandle) {
            RemoveVectoredExceptionHandler(g_vehHandle);
            g_vehHandle = NULL;
        }

        /* Remove all hardware breakpoints */
        for (i = 0; i < 4; i++)
            RemoveHardwareBp(i);

        if (g_shm)
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

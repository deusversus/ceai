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
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>

/* ── Shared memory layout ── */

#define SHM_MAGIC           0xCEAE
#define SHM_VERSION         3
#define SHM_HEADER_SIZE     0x40
#define HIT_ENTRY_SIZE      192
#define DEFAULT_MAX_HITS    4096

/* Commands (host → agent) */
#define CMD_IDLE            0
#define CMD_SET_BP          1
#define CMD_REMOVE_BP       2
#define CMD_SHUTDOWN        3
#define CMD_REFRESH_THREADS 4
#define CMD_ENABLE_STEALTH  5
#define CMD_DISABLE_STEALTH 6
#define CMD_START_TRACE     7
#define CMD_STOP_TRACE      8
#define CMD_SET_PAGE_GUARD  9
#define CMD_REMOVE_PAGE_GUARD 10
#define CMD_SET_INT3        11
#define CMD_REMOVE_INT3     12

/* Agent status */
#define STATUS_LOADING      0
#define STATUS_READY        1
#define STATUS_ERROR        2
#define STATUS_SHUTDOWN     3

/* BP types (matches VehBreakpointType enum) */
#define BP_EXECUTE          0
#define BP_WRITE            1
#define BP_READWRITE        2
#define BP_TRACE            3   /* trace step entry */
#define BP_PAGE_GUARD_READ  4
#define BP_PAGE_GUARD_WRITE 5
#define BP_SOFTWARE         6   /* INT3 software breakpoint */

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
    /* V3: Extended registers */
    ULONG64 rip;            /* 0x80 */
    ULONG64 r12, r13, r14, r15;  /* 0x88-0xA8 */
    ULONG64 eflags;         /* 0xA8 */
    ULONG64 _reserved;      /* 0xB0 — pad to 192 bytes */
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

/* ── Page Guard + INT3 globals ── */

#define MAX_PAGE_GUARDS 64
#define MAX_INT3_BPS    64
#define PAGE_SIZE       0x1000

typedef struct {
    ULONG64 pageBase;       /* page-aligned base address */
    DWORD   origProtection; /* original VirtualProtect flags */
    volatile BOOL active;
    volatile BOOL pendingRearm; /* set when guard consumed, cleared after re-arm */
} PageGuardEntry;

typedef struct {
    ULONG64 address;
    BYTE    origByte;
    volatile BOOL active;
    volatile BOOL pendingRearm; /* set during single-step re-arm cycle */
} Int3BpEntry;

static PageGuardEntry  g_pageGuards[MAX_PAGE_GUARDS] = {{0}};
static Int3BpEntry     g_int3Bps[MAX_INT3_BPS] = {{0}};

/* ── Trace globals ── */

static volatile BOOL    g_traceActive = FALSE;
static volatile LONG    g_traceStepsRemaining = 0;
static DWORD            g_traceThreadFilter = 0;  /* 0 = all threads */

/* ── Stealth globals ── */

static volatile BOOL    g_stealthActive = FALSE;

/* NtGetThreadContext hook: we detour this to zero DR0-DR7 in the output CONTEXT.
 * The trampoline stores the original prologue bytes so we can call the real function. */
#ifdef _M_IX86
#define HOOK_PROLOGUE_SIZE 5   /* 5-byte JMP rel32 on x86 */
#else
#define HOOK_PROLOGUE_SIZE 14  /* 14-byte MOV RAX, imm64; JMP RAX on x64 */
#endif
#define TRAMPOLINE_SIZE    64  /* enough for prologue + JMP back */

static BYTE*            g_hookTrampoline = NULL;    /* executable trampoline memory */
static BYTE             g_hookOrigBytes[HOOK_PROLOGUE_SIZE] = {0};
static BYTE*            g_hookTarget = NULL;        /* NtGetThreadContext entry point */
static volatile BOOL    g_hookInstalled = FALSE;

/* PEB module hiding state */
static volatile BOOL    g_moduleHidden = FALSE;

/* NtGetThreadContext typedef — NTSTATUS (NTAPI*)(HANDLE, PCONTEXT) */
typedef LONG (NTAPI *PFN_NtGetThreadContext)(HANDLE ThreadHandle, PCONTEXT Context);

/* ── Forward declarations ── */

static LONG NTAPI VehHandler(PEXCEPTION_POINTERS ep);
static DWORD WINAPI CommandThread(LPVOID param);
static BOOL SetHardwareBp(ULONG64 address, DWORD type, DWORD drSlot, DWORD dataSize);
static BOOL RemoveHardwareBp(DWORD drSlot);
static void RefreshThreadBreakpoints(void);
static void WriteHitEntry(PCONTEXT ctx, ULONG64 dr6);
static void WriteTraceEntry(PCONTEXT ctx);
static void WritePageGuardHit(PCONTEXT ctx, ULONG64 faultAddr, DWORD hitType);
static void WriteInt3Hit(PCONTEXT ctx, ULONG64 bpAddr);
static BOOL SetPageGuardBp(ULONG64 address);
static BOOL RemovePageGuardBp(ULONG64 address);
static BOOL SetInt3Bp(ULONG64 address);
static BOOL RemoveInt3Bp(ULONG64 address);
static BOOL EnableStealth(void);
static BOOL DisableStealth(void);
static BOOL InstallNtGetThreadContextHook(void);
static void RemoveNtGetThreadContextHook(void);
static void HideModuleFromPeb(HMODULE hModule);
static void UnhideModuleInPeb(void);

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
    PCONTEXT ctx;
    ULONG64 dr6;
    DWORD exCode = ep->ExceptionRecord->ExceptionCode;

    if (!g_shm || g_shm->agentStatus != STATUS_READY)
        return EXCEPTION_CONTINUE_SEARCH;

    ctx = ep->ContextRecord;

    /* ── PAGE_GUARD violation (0x80000001) ── */
    if (exCode == (DWORD)0x80000001UL) /* STATUS_GUARD_PAGE_VIOLATION */
    {
        ULONG64 faultAddr;
        DWORD accessType; /* 0=read, 1=write, 8=execute */
        int i;
        BOOL found = FALSE;

        /* ExceptionInformation[0] = access type, [1] = fault address */
        accessType = (DWORD)ep->ExceptionRecord->ExceptionInformation[0];
        faultAddr = (ULONG64)ep->ExceptionRecord->ExceptionInformation[1];

        /* Check if this page is one of ours */
        for (i = 0; i < MAX_PAGE_GUARDS; i++)
        {
            if (g_pageGuards[i].active &&
                faultAddr >= g_pageGuards[i].pageBase &&
                faultAddr < g_pageGuards[i].pageBase + PAGE_SIZE)
            {
                found = TRUE;
                break;
            }
        }

        if (!found)
            return EXCEPTION_CONTINUE_SEARCH;

        /* Record hit */
        {
            DWORD hitType = (accessType == 1) ? BP_PAGE_GUARD_WRITE : BP_PAGE_GUARD_READ;
            WritePageGuardHit(ctx, faultAddr, hitType);
        }

        /* PAGE_GUARD is one-shot — Windows already removed it on violation.
         * Mark this specific page for re-arm, then set TF to single-step past
         * the faulting instruction. The re-arm handler only re-arms pages with
         * pendingRearm == TRUE, avoiding unnecessary VirtualProtect calls. */
        g_pageGuards[i].pendingRearm = TRUE;
        ctx->EFlags |= 0x100; /* TF */

        if (g_hitEvent) SetEvent(g_hitEvent);
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    /* ── INT3 software breakpoint (0x80000003) ── */
    if (exCode == (DWORD)0x80000003UL) /* EXCEPTION_BREAKPOINT */
    {
        ULONG64 bpAddr;
        int i;
        BOOL found = FALSE;
        DWORD oldProt;

#ifdef _M_IX86
        bpAddr = (ULONG64)ctx->Eip;
#else
        bpAddr = ctx->Rip;
#endif
        /* INT3 increments IP past the 0xCC — subtract 1 to get the real BP address */
        bpAddr -= 1;

        for (i = 0; i < MAX_INT3_BPS; i++)
        {
            if (g_int3Bps[i].active && g_int3Bps[i].address == bpAddr)
            {
                found = TRUE;
                break;
            }
        }

        if (!found)
            return EXCEPTION_CONTINUE_SEARCH;

        /* Record hit */
        WriteInt3Hit(ctx, bpAddr);

        /* Restore original byte, set IP back, set TF for single-step, mark pending re-arm */
        if (VirtualProtect((LPVOID)(ULONG_PTR)bpAddr, 1, PAGE_EXECUTE_READWRITE, &oldProt))
        {
            *(BYTE*)(ULONG_PTR)bpAddr = g_int3Bps[i].origByte;
            FlushInstructionCache(GetCurrentProcess(), (LPCVOID)(ULONG_PTR)bpAddr, 1);
            VirtualProtect((LPVOID)(ULONG_PTR)bpAddr, 1, oldProt, &oldProt);
        }
#ifdef _M_IX86
        ctx->Eip = (DWORD)bpAddr; /* back up IP to the original instruction */
#else
        ctx->Rip = bpAddr;
#endif
        ctx->EFlags |= 0x100; /* TF — single-step, then re-arm 0xCC */
        g_int3Bps[i].pendingRearm = TRUE;

        if (g_hitEvent) SetEvent(g_hitEvent);
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    /* ── EXCEPTION_SINGLE_STEP (0x80000004) — hardware BPs + trace + re-arm ── */
    if (exCode != EXCEPTION_SINGLE_STEP)
        return EXCEPTION_CONTINUE_SEARCH;

    dr6 = ctx->Dr6;

    /* ── Re-arm PAGE_GUARD after single-step past the faulting instruction ── */
    if (dr6 & 0x4000) /* BS flag — this was a TF single-step */
    {
        int i;
        BOOL rearmed = FALSE;

        /* Re-arm only PAGE_GUARD entries that were consumed (pendingRearm) */
        for (i = 0; i < MAX_PAGE_GUARDS; i++)
        {
            DWORD oldProt;
            if (!g_pageGuards[i].active || !g_pageGuards[i].pendingRearm) continue;
            /* Re-apply PAGE_GUARD to the faulting page */
            VirtualProtect((LPVOID)(ULONG_PTR)g_pageGuards[i].pageBase, PAGE_SIZE,
                g_pageGuards[i].origProtection | 0x100 /* PAGE_GUARD */, &oldProt);
            g_pageGuards[i].pendingRearm = FALSE;
            rearmed = TRUE;
        }

        /* Re-arm any INT3 breakpoints pending re-arm */
        for (i = 0; i < MAX_INT3_BPS; i++)
        {
            DWORD oldProt;
            if (!g_int3Bps[i].active || !g_int3Bps[i].pendingRearm) continue;
            if (VirtualProtect((LPVOID)(ULONG_PTR)g_int3Bps[i].address, 1, PAGE_EXECUTE_READWRITE, &oldProt))
            {
                *(BYTE*)(ULONG_PTR)g_int3Bps[i].address = 0xCC;
                FlushInstructionCache(GetCurrentProcess(), (LPCVOID)(ULONG_PTR)g_int3Bps[i].address, 1);
                VirtualProtect((LPVOID)(ULONG_PTR)g_int3Bps[i].address, 1, oldProt, &oldProt);
            }
            g_int3Bps[i].pendingRearm = FALSE;
            rearmed = TRUE;
        }

        /* If we re-armed something and no DR0-3 bits set, consume the single-step */
        if (rearmed && (dr6 & 0xF) == 0 && !g_traceActive)
        {
            ctx->EFlags &= ~0x100UL; /* Clear TF */
            ctx->Dr6 = 0;
            return EXCEPTION_CONTINUE_EXECUTION;
        }
    }

    /* M2 fix: Handle orphaned TF — if DR6 bit 14 (BS) is set but trace is NOT active,
     * the trace was stopped mid-flight. Clear TF to prevent unhandled exception crash. */
    if ((dr6 & 0x4000) && !g_traceActive)
    {
        ctx->EFlags &= ~0x100UL; /* Clear TF */
        ctx->Dr6 &= ~0x4000ULL; /* Clear BS */
        /* If hardware BP bits also set, fall through to handle them below */
        if ((dr6 & 0xF) == 0)
            return EXCEPTION_CONTINUE_EXECUTION; /* Pure orphaned TF — consume silently */
        dr6 &= ~0x4000ULL; /* Strip BS for the hardware BP handler below */
    }

    /* Check DR6 bit 14 (BS = single-step / Trap Flag) for active trace mode */
    if ((dr6 & 0x4000) && g_traceActive)
    {
        DWORD tid = GetCurrentThreadId();
        /* Thread filter: if set, only trace on the specified thread */
        if (g_traceThreadFilter != 0 && tid != g_traceThreadFilter)
        {
            /* Not our trace thread — clear TF and BS, consume silently */
            ctx->EFlags &= ~0x100UL;
            ctx->Dr6 &= ~0x4000ULL;
            /* If hardware BP bits also set, fall through */
            if ((dr6 & 0xF) == 0)
                return EXCEPTION_CONTINUE_EXECUTION;
        }
        else
        {
            /* Record trace step as hitType = BP_TRACE */
            WriteTraceEntry(ctx);

            /* Decrement steps remaining */
            if (InterlockedDecrement(&g_traceStepsRemaining) > 0)
            {
                ctx->EFlags |= 0x100; /* TF = keep stepping */
            }
            else
            {
                ctx->EFlags &= ~0x100UL; /* Trace complete — clear TF */
                g_traceActive = FALSE;
            }

            /* C2 fix: If hardware BP bits are ALSO set (stepped onto a BP address),
             * fall through to record the BP hit too instead of returning early. */
            if ((dr6 & 0xF) == 0)
            {
                ctx->Dr6 = 0;
                if (g_hitEvent) SetEvent(g_hitEvent);
                return EXCEPTION_CONTINUE_EXECUTION;
            }
            /* else: both trace step AND hardware BP — continue to BP handler below */
        }
    }

    /* Check if any of DR0-DR3 triggered (bits 0-3 of DR6) */
    if ((dr6 & 0xF) == 0)
        return EXCEPTION_CONTINUE_SEARCH;

    WriteHitEntry(ctx, dr6);

    /* If trace is active and this is a hardware BP hit, start stepping */
    if (g_traceActive && g_traceStepsRemaining > 0)
    {
        DWORD tid = GetCurrentThreadId();
        if (g_traceThreadFilter == 0 || tid == g_traceThreadFilter)
        {
            ctx->EFlags |= 0x100; /* Set TF to begin single-stepping */
        }
    }

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
    LONG readIdx;
    LONG slot;

    /* CAS loop: atomically claim a slot only if the ring isn't full.
     * Prevents TOCTOU race where concurrent VEH hits bypass the overflow check. */
    {
        LONG claimed;
        do {
            claimed = InterlockedCompareExchange(&g_shm->hitWriteIndex, 0, 0);
            readIdx = InterlockedCompareExchange(&g_shm->hitReadIndex, 0, 0);
            if ((DWORD)(claimed - readIdx) >= g_maxHits)
            {
                InterlockedIncrement(&g_shm->overflowCount);
                return;
            }
        } while (InterlockedCompareExchange(&g_shm->hitWriteIndex, claimed + 1, claimed) != claimed);
        slot = (LONG)((DWORD)claimed % g_maxHits);
    }

    {
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
        /* V3: Extended registers */
        hit->rip = (ULONG64)ctx->Eip;
        hit->r12 = 0; hit->r13 = 0; hit->r14 = 0; hit->r15 = 0;
        hit->eflags = ctx->EFlags;
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
        /* V3: Extended registers */
        hit->rip = ctx->Rip;
        hit->r12 = ctx->R12; hit->r13 = ctx->R13;
        hit->r14 = ctx->R14; hit->r15 = ctx->R15;
        hit->eflags = ctx->EFlags;
#endif
        hit->timestamp = (ULONG64)ts.QuadPart;
        hit->_reserved = 0;
    }

    InterlockedIncrement(&g_shm->hitCount);
}

/* ── Trace entry write (separate from BP hits — always uses hitType = BP_TRACE) ── */

static void WriteTraceEntry(PCONTEXT ctx)
{
    LONG readIdx;
    LONG slot;

    /* CAS loop: atomically claim a slot only if the ring isn't full. */
    {
        LONG claimed;
        do {
            claimed = InterlockedCompareExchange(&g_shm->hitWriteIndex, 0, 0);
            readIdx = InterlockedCompareExchange(&g_shm->hitReadIndex, 0, 0);
            if ((DWORD)(claimed - readIdx) >= g_maxHits)
            {
                InterlockedIncrement(&g_shm->overflowCount);
                return;
            }
        } while (InterlockedCompareExchange(&g_shm->hitWriteIndex, claimed + 1, claimed) != claimed);
        slot = (LONG)((DWORD)claimed % g_maxHits);
    }

    {
        BYTE* base;
        HitEntry* hit;
        LARGE_INTEGER ts;
        base = (BYTE*)g_shm + SHM_HEADER_SIZE + slot * HIT_ENTRY_SIZE;
        hit = (HitEntry*)base;

        QueryPerformanceCounter(&ts);

#ifdef _M_IX86
        hit->address  = (ULONG64)ctx->Eip;
        hit->threadId = GetCurrentThreadId();
        hit->hitType  = BP_TRACE;
        hit->dr6      = 0x4000; /* BS flag */
        hit->rax = ctx->Eax; hit->rbx = ctx->Ebx;
        hit->rcx = ctx->Ecx; hit->rdx = ctx->Edx;
        hit->rsi = ctx->Esi; hit->rdi = ctx->Edi;
        hit->rsp = ctx->Esp; hit->rbp = ctx->Ebp;
        hit->r8  = 0;        hit->r9  = 0;
        hit->r10 = 0;        hit->r11 = 0;
        hit->rip = (ULONG64)ctx->Eip;
        hit->r12 = 0; hit->r13 = 0; hit->r14 = 0; hit->r15 = 0;
        hit->eflags = ctx->EFlags;
#else
        hit->address  = ctx->Rip;
        hit->threadId = GetCurrentThreadId();
        hit->hitType  = BP_TRACE;
        hit->dr6      = 0x4000; /* BS flag */
        hit->rax = ctx->Rax; hit->rbx = ctx->Rbx;
        hit->rcx = ctx->Rcx; hit->rdx = ctx->Rdx;
        hit->rsi = ctx->Rsi; hit->rdi = ctx->Rdi;
        hit->rsp = ctx->Rsp; hit->rbp = ctx->Rbp;
        hit->r8  = ctx->R8;  hit->r9  = ctx->R9;
        hit->r10 = ctx->R10; hit->r11 = ctx->R11;
        hit->rip = ctx->Rip;
        hit->r12 = ctx->R12; hit->r13 = ctx->R13;
        hit->r14 = ctx->R14; hit->r15 = ctx->R15;
        hit->eflags = ctx->EFlags;
#endif
        hit->timestamp = (ULONG64)ts.QuadPart;
        hit->_reserved = 0;
    }

    InterlockedIncrement(&g_shm->hitCount);
}

/* ── Page Guard hit entry ── */

static void WritePageGuardHit(PCONTEXT ctx, ULONG64 faultAddr, DWORD hitType)
{
    LONG readIdx, slot;

    /* CAS loop: atomically claim a slot only if the ring isn't full. */
    {
        LONG claimed;
        do {
            claimed = InterlockedCompareExchange(&g_shm->hitWriteIndex, 0, 0);
            readIdx = InterlockedCompareExchange(&g_shm->hitReadIndex, 0, 0);
            if ((DWORD)(claimed - readIdx) >= g_maxHits)
            {
                InterlockedIncrement(&g_shm->overflowCount);
                return;
            }
        } while (InterlockedCompareExchange(&g_shm->hitWriteIndex, claimed + 1, claimed) != claimed);
        slot = (LONG)((DWORD)claimed % g_maxHits);
    }
    {
        BYTE* base = (BYTE*)g_shm + SHM_HEADER_SIZE + slot * HIT_ENTRY_SIZE;
        HitEntry* hit = (HitEntry*)base;
        LARGE_INTEGER ts;
        QueryPerformanceCounter(&ts);
#ifdef _M_IX86
        hit->address = faultAddr;
        hit->threadId = GetCurrentThreadId();
        hit->hitType = hitType;
        hit->dr6 = 0;
        hit->rax = ctx->Eax; hit->rbx = ctx->Ebx;
        hit->rcx = ctx->Ecx; hit->rdx = ctx->Edx;
        hit->rsi = ctx->Esi; hit->rdi = ctx->Edi;
        hit->rsp = ctx->Esp; hit->rbp = ctx->Ebp;
        hit->r8 = 0; hit->r9 = 0; hit->r10 = 0; hit->r11 = 0;
        hit->rip = (ULONG64)ctx->Eip; hit->r12 = 0; hit->r13 = 0; hit->r14 = 0; hit->r15 = 0;
        hit->eflags = ctx->EFlags;
#else
        hit->address = faultAddr;
        hit->threadId = GetCurrentThreadId();
        hit->hitType = hitType;
        hit->dr6 = 0;
        hit->rax = ctx->Rax; hit->rbx = ctx->Rbx;
        hit->rcx = ctx->Rcx; hit->rdx = ctx->Rdx;
        hit->rsi = ctx->Rsi; hit->rdi = ctx->Rdi;
        hit->rsp = ctx->Rsp; hit->rbp = ctx->Rbp;
        hit->r8 = ctx->R8; hit->r9 = ctx->R9;
        hit->r10 = ctx->R10; hit->r11 = ctx->R11;
        hit->rip = ctx->Rip; hit->r12 = ctx->R12; hit->r13 = ctx->R13;
        hit->r14 = ctx->R14; hit->r15 = ctx->R15;
        hit->eflags = ctx->EFlags;
#endif
        hit->timestamp = (ULONG64)ts.QuadPart;
        hit->_reserved = 0;
    }
    InterlockedIncrement(&g_shm->hitCount);
}

/* ── INT3 hit entry ── */

static void WriteInt3Hit(PCONTEXT ctx, ULONG64 bpAddr)
{
    LONG readIdx, slot;

    /* CAS loop: atomically claim a slot only if the ring isn't full. */
    {
        LONG claimed;
        do {
            claimed = InterlockedCompareExchange(&g_shm->hitWriteIndex, 0, 0);
            readIdx = InterlockedCompareExchange(&g_shm->hitReadIndex, 0, 0);
            if ((DWORD)(claimed - readIdx) >= g_maxHits)
            {
                InterlockedIncrement(&g_shm->overflowCount);
                return;
            }
        } while (InterlockedCompareExchange(&g_shm->hitWriteIndex, claimed + 1, claimed) != claimed);
        slot = (LONG)((DWORD)claimed % g_maxHits);
    }
    {
        BYTE* base = (BYTE*)g_shm + SHM_HEADER_SIZE + slot * HIT_ENTRY_SIZE;
        HitEntry* hit = (HitEntry*)base;
        LARGE_INTEGER ts;
        QueryPerformanceCounter(&ts);
#ifdef _M_IX86
        hit->address = bpAddr;
        hit->threadId = GetCurrentThreadId();
        hit->hitType = BP_SOFTWARE;
        hit->dr6 = 0;
        hit->rax = ctx->Eax; hit->rbx = ctx->Ebx;
        hit->rcx = ctx->Ecx; hit->rdx = ctx->Edx;
        hit->rsi = ctx->Esi; hit->rdi = ctx->Edi;
        hit->rsp = ctx->Esp; hit->rbp = ctx->Ebp;
        hit->r8 = 0; hit->r9 = 0; hit->r10 = 0; hit->r11 = 0;
        hit->rip = (ULONG64)ctx->Eip; hit->r12 = 0; hit->r13 = 0; hit->r14 = 0; hit->r15 = 0;
        hit->eflags = ctx->EFlags;
#else
        hit->address = bpAddr;
        hit->threadId = GetCurrentThreadId();
        hit->hitType = BP_SOFTWARE;
        hit->dr6 = 0;
        hit->rax = ctx->Rax; hit->rbx = ctx->Rbx;
        hit->rcx = ctx->Rcx; hit->rdx = ctx->Rdx;
        hit->rsi = ctx->Rsi; hit->rdi = ctx->Rdi;
        hit->rsp = ctx->Rsp; hit->rbp = ctx->Rbp;
        hit->r8 = ctx->R8; hit->r9 = ctx->R9;
        hit->r10 = ctx->R10; hit->r11 = ctx->R11;
        hit->rip = ctx->Rip; hit->r12 = ctx->R12; hit->r13 = ctx->R13;
        hit->r14 = ctx->R14; hit->r15 = ctx->R15;
        hit->eflags = ctx->EFlags;
#endif
        hit->timestamp = (ULONG64)ts.QuadPart;
        hit->_reserved = 0;
    }
    InterlockedIncrement(&g_shm->hitCount);
}

/* ── Page Guard BP set/remove ── */

static BOOL SetPageGuardBp(ULONG64 address)
{
    ULONG64 pageBase = address & ~((ULONG64)PAGE_SIZE - 1);
    DWORD oldProt;
    int freeSlot = -1;
    int i;

    /* Find existing or free slot */
    for (i = 0; i < MAX_PAGE_GUARDS; i++)
    {
        if (g_pageGuards[i].active && g_pageGuards[i].pageBase == pageBase)
            return TRUE; /* already guarded */
        if (!g_pageGuards[i].active && freeSlot == -1)
            freeSlot = i;
    }
    if (freeSlot == -1) return FALSE; /* no free slots */

    /* Query current protection */
    {
        MEMORY_BASIC_INFORMATION mbi;
        if (!VirtualQuery((LPCVOID)(ULONG_PTR)pageBase, &mbi, sizeof(mbi)))
            return FALSE;
        oldProt = mbi.Protect & 0xFF; /* mask off PAGE_GUARD if somehow set */
    }

    /* Apply PAGE_GUARD */
    if (!VirtualProtect((LPVOID)(ULONG_PTR)pageBase, PAGE_SIZE, oldProt | 0x100, &oldProt))
        return FALSE;

    g_pageGuards[freeSlot].pageBase = pageBase;
    g_pageGuards[freeSlot].origProtection = oldProt;
    InterlockedExchange((volatile LONG*)&g_pageGuards[freeSlot].active, TRUE);
    return TRUE;
}

static BOOL RemovePageGuardBp(ULONG64 address)
{
    ULONG64 pageBase = address & ~((ULONG64)PAGE_SIZE - 1);
    DWORD dummy;
    int i;

    for (i = 0; i < MAX_PAGE_GUARDS; i++)
    {
        if (g_pageGuards[i].active && g_pageGuards[i].pageBase == pageBase)
        {
            /* Restore original protection (without PAGE_GUARD) */
            VirtualProtect((LPVOID)(ULONG_PTR)pageBase, PAGE_SIZE,
                g_pageGuards[i].origProtection, &dummy);
            g_pageGuards[i].pendingRearm = FALSE; /* prevent stale re-arm on slot reuse */
            InterlockedExchange((volatile LONG*)&g_pageGuards[i].active, FALSE);
            return TRUE;
        }
    }
    return FALSE;
}

/* ── INT3 BP set/remove ── */

static BOOL SetInt3Bp(ULONG64 address)
{
    DWORD oldProt;
    BYTE origByte;
    int freeSlot = -1;
    int i;

    /* Find existing or free slot */
    for (i = 0; i < MAX_INT3_BPS; i++)
    {
        if (g_int3Bps[i].active && g_int3Bps[i].address == address)
            return TRUE; /* already set */
        if (!g_int3Bps[i].active && freeSlot == -1)
            freeSlot = i;
    }
    if (freeSlot == -1) return FALSE;

    /* Read original byte — SEH-protected against unreadable pages */
    __try {
        origByte = *(BYTE*)(ULONG_PTR)address;
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return FALSE; /* page not readable */
    }
    if (origByte == 0xCC) return TRUE; /* already INT3 */

    /* Write 0xCC */
    if (!VirtualProtect((LPVOID)(ULONG_PTR)address, 1, PAGE_EXECUTE_READWRITE, &oldProt))
        return FALSE;
    *(BYTE*)(ULONG_PTR)address = 0xCC;
    FlushInstructionCache(GetCurrentProcess(), (LPCVOID)(ULONG_PTR)address, 1);
    VirtualProtect((LPVOID)(ULONG_PTR)address, 1, oldProt, &oldProt);

    g_int3Bps[freeSlot].address = address;
    g_int3Bps[freeSlot].origByte = origByte;
    g_int3Bps[freeSlot].pendingRearm = FALSE;
    InterlockedExchange((volatile LONG*)&g_int3Bps[freeSlot].active, TRUE);
    return TRUE;
}

static BOOL RemoveInt3Bp(ULONG64 address)
{
    DWORD oldProt;
    int i;

    for (i = 0; i < MAX_INT3_BPS; i++)
    {
        if (g_int3Bps[i].active && g_int3Bps[i].address == address)
        {
            if (VirtualProtect((LPVOID)(ULONG_PTR)address, 1, PAGE_EXECUTE_READWRITE, &oldProt))
            {
                *(BYTE*)(ULONG_PTR)address = g_int3Bps[i].origByte;
                FlushInstructionCache(GetCurrentProcess(), (LPCVOID)(ULONG_PTR)address, 1);
                VirtualProtect((LPVOID)(ULONG_PTR)address, 1, oldProt, &oldProt);
            }
            g_int3Bps[i].pendingRearm = FALSE; /* prevent stale re-arm on slot reuse */
            InterlockedExchange((volatile LONG*)&g_int3Bps[i].active, FALSE);
            return TRUE;
        }
    }
    return FALSE;
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
                    case 0: ctx.Dr0 = (DWORD_PTR)address; break;
                    case 1: ctx.Dr1 = (DWORD_PTR)address; break;
                    case 2: ctx.Dr2 = (DWORD_PTR)address; break;
                    case 3: ctx.Dr3 = (DWORD_PTR)address; break;
                }
                ctx.Dr7 = (DWORD_PTR)EnableDr7Slot(ctx.Dr7, drSlot, type, dataSize);
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
                ctx.Dr7 = (DWORD_PTR)DisableDr7Slot(ctx.Dr7, drSlot);
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
                                case 0: ctx.Dr0 = (DWORD_PTR)g_bpAddresses[i]; break;
                                case 1: ctx.Dr1 = (DWORD_PTR)g_bpAddresses[i]; break;
                                case 2: ctx.Dr2 = (DWORD_PTR)g_bpAddresses[i]; break;
                                case 3: ctx.Dr3 = (DWORD_PTR)g_bpAddresses[i]; break;
                            }
                            ctx.Dr7 = (DWORD_PTR)EnableDr7Slot(ctx.Dr7, i, g_bpTypes[i], g_bpSizes[i]);
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

            case CMD_ENABLE_STEALTH:
                result = EnableStealth() ? 0 : -1;
                break;

            case CMD_DISABLE_STEALTH:
                result = DisableStealth() ? 0 : -1;
                break;

            case CMD_START_TRACE:
                /* commandArg0 = unused (trace starts on next HW BP hit)
                 * commandArg1 = maxSteps
                 * commandArg2 = threadFilter (0 = all) */
                g_traceThreadFilter = g_shm->commandArg2;
                g_traceStepsRemaining = (LONG)g_shm->commandArg1;
                InterlockedExchange((volatile LONG*)&g_traceActive, TRUE);
                result = 0;
                break;

            case CMD_STOP_TRACE:
                InterlockedExchange((volatile LONG*)&g_traceActive, FALSE);
                g_traceStepsRemaining = 0;
                result = 0;
                break;

            case CMD_SET_PAGE_GUARD:
                result = SetPageGuardBp(g_shm->commandArg0) ? 0 : -1;
                break;

            case CMD_REMOVE_PAGE_GUARD:
                result = RemovePageGuardBp(g_shm->commandArg0) ? 0 : -1;
                break;

            case CMD_SET_INT3:
                result = SetInt3Bp(g_shm->commandArg0) ? 0 : -1;
                break;

            case CMD_REMOVE_INT3:
                result = RemoveInt3Bp(g_shm->commandArg0) ? 0 : -1;
                break;

            case CMD_SHUTDOWN:
                /* Disable stealth before shutdown to re-link module for clean FreeLibrary */
                if (g_stealthActive) DisableStealth();
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

/* ── Stealth: NtGetThreadContext hook ── */

/*
 * Our detour function. Called instead of NtGetThreadContext when the hook is active.
 * Calls the original via the trampoline, then zeros DR0-DR7 in the returned CONTEXT
 * if CONTEXT_DEBUG_REGISTERS was requested. This hides hardware breakpoints from
 * anti-cheat GetThreadContext checks.
 */
static LONG NTAPI HookedNtGetThreadContext(HANDLE ThreadHandle, PCONTEXT Context)
{
    /* Call original via trampoline */
    PFN_NtGetThreadContext pOriginal = (PFN_NtGetThreadContext)g_hookTrampoline;
    LONG status = pOriginal(ThreadHandle, Context);

    /* If call succeeded and debug registers were requested, zero them */
    if (status >= 0 && Context != NULL)
    {
#ifdef _M_IX86
        if (Context->ContextFlags & 0x00010010) /* CONTEXT_i386 | CONTEXT_DEBUG_REGISTERS */
#else
        if (Context->ContextFlags & 0x00100010) /* CONTEXT_AMD64 | CONTEXT_DEBUG_REGISTERS */
#endif
        {
            Context->Dr0 = 0;
            Context->Dr1 = 0;
            Context->Dr2 = 0;
            Context->Dr3 = 0;
            Context->Dr6 = 0;
            Context->Dr7 = 0;
        }
    }

    return status;
}

/* ── Thread suspension helper for safe code patching (C2 fix) ── */

static void SuspendAllThreadsExceptSelf(void)
{
    HANDLE snap;
    THREADENTRY32 te;
    DWORD pid = GetCurrentProcessId();
    DWORD myTid = GetCurrentThreadId();

    snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    te.dwSize = sizeof(te);
    if (Thread32First(snap, &te)) {
        do {
            HANDLE hThread;
            if (te.th32OwnerProcessID != pid || te.th32ThreadID == myTid) continue;
            hThread = OpenThread(THREAD_SUSPEND_RESUME, FALSE, te.th32ThreadID);
            if (hThread) { SuspendThread(hThread); CloseHandle(hThread); }
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
}

static void ResumeAllThreadsExceptSelf(void)
{
    HANDLE snap;
    THREADENTRY32 te;
    DWORD pid = GetCurrentProcessId();
    DWORD myTid = GetCurrentThreadId();

    snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    te.dwSize = sizeof(te);
    if (Thread32First(snap, &te)) {
        do {
            HANDLE hThread;
            if (te.th32OwnerProcessID != pid || te.th32ThreadID == myTid) continue;
            hThread = OpenThread(THREAD_SUSPEND_RESUME, FALSE, te.th32ThreadID);
            if (hThread) { ResumeThread(hThread); CloseHandle(hThread); }
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
}

/* ── Prologue validation (C1 fix) ── */

/*
 * Validate that the NtGetThreadContext prologue matches the expected ntdll
 * syscall stub layout. On Win10/11 x64 this is:
 *   4C 8B D1          mov r10, rcx
 *   B8 xx xx 00 00    mov eax, <syscall_number>
 *   ...               (test/syscall/etc)
 * We check the first 3 bytes to confirm it's the expected pattern.
 * If the layout changes, we refuse to hook rather than crash.
 */
static BOOL ValidatePrologue(const BYTE* target)
{
#ifdef _M_IX86
    /* x86 ntdll stubs: B8 xx xx xx xx (mov eax, syscall#) */
    return (target[0] == 0xB8);
#else
    /* x64: 4C 8B D1 = mov r10, rcx (standard syscall stub prefix) */
    return (target[0] == 0x4C && target[1] == 0x8B && target[2] == 0xD1);
#endif
}

static BOOL InstallNtGetThreadContextHook(void)
{
    HMODULE hNtdll;
    DWORD oldProtect;
    BYTE* target;

    if (g_hookInstalled) return TRUE; /* already hooked */

    hNtdll = GetModuleHandleW(L"ntdll.dll");
    if (!hNtdll) return FALSE;

    target = (BYTE*)GetProcAddress(hNtdll, "NtGetThreadContext");
    if (!target) return FALSE;

    /* C1: Validate prologue matches expected syscall stub layout */
    if (!ValidatePrologue(target)) return FALSE;

    g_hookTarget = target;

    /* Allocate executable trampoline memory */
    g_hookTrampoline = (BYTE*)VirtualAlloc(NULL, TRAMPOLINE_SIZE,
        MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!g_hookTrampoline) return FALSE;

    /* Save original prologue bytes */
    memcpy(g_hookOrigBytes, target, HOOK_PROLOGUE_SIZE);

    /* Build trampoline: original prologue bytes + register-preserving JMP back */
    memcpy(g_hookTrampoline, target, HOOK_PROLOGUE_SIZE);

#ifdef _M_IX86
    /* x86: 5-byte relative JMP back (doesn't clobber any registers) */
    {
        BYTE* jmpBack = g_hookTrampoline + HOOK_PROLOGUE_SIZE;
        BYTE* returnAddr = target + HOOK_PROLOGUE_SIZE;
        jmpBack[0] = 0xE9; /* JMP rel32 */
        *(DWORD*)(jmpBack + 1) = (DWORD)(returnAddr - (jmpBack + 5));
    }
#else
    /* x64 C3 FIX: Use indirect JMP via RIP-relative memory, NOT MOV RAX which
     * would clobber EAX (holding the syscall number set by the prologue).
     * Encoding: FF 25 00 00 00 00 = JMP QWORD PTR [RIP+0] (6 bytes)
     *           followed by the 8-byte absolute address (8 bytes)
     * Total: 14 bytes, clobbers NO registers. */
    {
        BYTE* jmpBack = g_hookTrampoline + HOOK_PROLOGUE_SIZE;
        ULONG64 returnAddr = (ULONG64)(target + HOOK_PROLOGUE_SIZE);
        jmpBack[0] = 0xFF; jmpBack[1] = 0x25;         /* JMP [RIP+0] */
        *(DWORD*)(jmpBack + 2) = 0x00000000;           /* disp32 = 0 */
        *(ULONG64*)(jmpBack + 6) = returnAddr;         /* 8-byte address */
    }
#endif

    /* C2: Suspend all threads before patching to prevent partial-instruction execution */
    SuspendAllThreadsExceptSelf();

    /* Patch original function entry: JMP to our hook */
    if (!VirtualProtect(target, HOOK_PROLOGUE_SIZE, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        ResumeAllThreadsExceptSelf();
        VirtualFree(g_hookTrampoline, 0, MEM_RELEASE);
        g_hookTrampoline = NULL;
        return FALSE;
    }

#ifdef _M_IX86
    /* x86: 5-byte relative JMP to detour */
    {
        DWORD hookOffset = (DWORD)((BYTE*)HookedNtGetThreadContext - (target + 5));
        target[0] = 0xE9; /* JMP rel32 */
        *(DWORD*)(target + 1) = hookOffset;
    }
#else
    /* x64: 14-byte indirect JMP to detour (register-preserving)
     * FF 25 00 00 00 00 = JMP [RIP+0], followed by 8-byte hook address */
    {
        ULONG64 hookAddr = (ULONG64)HookedNtGetThreadContext;
        target[0] = 0xFF; target[1] = 0x25;            /* JMP [RIP+0] */
        *(DWORD*)(target + 2) = 0x00000000;             /* disp32 = 0 */
        *(ULONG64*)(target + 6) = hookAddr;             /* 8-byte address */
    }
#endif

    FlushInstructionCache(GetCurrentProcess(), target, HOOK_PROLOGUE_SIZE);
    VirtualProtect(target, HOOK_PROLOGUE_SIZE, oldProtect, &oldProtect);

    /* Resume threads after patch is complete and caches flushed */
    ResumeAllThreadsExceptSelf();

    g_hookInstalled = TRUE;
    return TRUE;
}

static void RemoveNtGetThreadContextHook(void)
{
    DWORD oldProtect;
    if (!g_hookInstalled || !g_hookTarget) return;

    /* C2: Suspend threads during unhook too */
    SuspendAllThreadsExceptSelf();

    /* Restore original prologue bytes */
    if (VirtualProtect(g_hookTarget, HOOK_PROLOGUE_SIZE, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        memcpy(g_hookTarget, g_hookOrigBytes, HOOK_PROLOGUE_SIZE);
        FlushInstructionCache(GetCurrentProcess(), g_hookTarget, HOOK_PROLOGUE_SIZE);
        VirtualProtect(g_hookTarget, HOOK_PROLOGUE_SIZE, oldProtect, &oldProtect);
    }

    ResumeAllThreadsExceptSelf();

    /* Free trampoline */
    if (g_hookTrampoline)
    {
        VirtualFree(g_hookTrampoline, 0, MEM_RELEASE);
        g_hookTrampoline = NULL;
    }

    g_hookInstalled = FALSE;
    g_hookTarget = NULL;
}

/* ── Stealth: PEB module hiding ── */

/*
 * Unlink our DLL from the PEB's three module lists (InLoadOrder, InMemoryOrder,
 * InInitializationOrder). This hides the agent from EnumProcessModules,
 * Module32First/Next, and GetModuleHandle.
 *
 * NOTE: PEB structure offsets are stable across Win10/11 x64 and x86.
 * Re-linking is required before FreeLibrary (done in DisableStealth).
 */

#ifdef _M_IX86
/* x86: PEB at fs:[0x30], Ldr at PEB+0x0C */
#define PEB_LDR_OFFSET 0x0C
#else
/* x64: PEB at gs:[0x60], Ldr at PEB+0x18 */
#define PEB_LDR_OFFSET 0x18
#endif

typedef struct _UNICODE_STRING_INTERNAL {
    USHORT Length;
    USHORT MaximumLength;
    WCHAR* Buffer;
} UNICODE_STRING_INTERNAL;

typedef struct _LDR_DATA_TABLE_ENTRY_INTERNAL {
    LIST_ENTRY InLoadOrderLinks;
    LIST_ENTRY InMemoryOrderLinks;
    LIST_ENTRY InInitializationOrderLinks;
    PVOID DllBase;
    PVOID EntryPoint;
    ULONG SizeOfImage;
    UNICODE_STRING_INTERNAL FullDllName;
    UNICODE_STRING_INTERNAL BaseDllName;
} LDR_DATA_TABLE_ENTRY_INTERNAL;

typedef struct _PEB_LDR_DATA_INTERNAL {
    ULONG Length;
    BOOLEAN Initialized;
    PVOID SsHandle;
    LIST_ENTRY InLoadOrderModuleList;
    LIST_ENTRY InMemoryOrderModuleList;
    LIST_ENTRY InInitializationOrderModuleList;
} PEB_LDR_DATA_INTERNAL;

/* Saved link pointers for re-linking on DisableStealth */
static LIST_ENTRY* g_savedLoadOrderFlink = NULL;
static LIST_ENTRY* g_savedLoadOrderBlink = NULL;
static LIST_ENTRY* g_savedMemOrderFlink = NULL;
static LIST_ENTRY* g_savedMemOrderBlink = NULL;
static LIST_ENTRY* g_savedInitOrderFlink = NULL;
static LIST_ENTRY* g_savedInitOrderBlink = NULL;
static LDR_DATA_TABLE_ENTRY_INTERNAL* g_hiddenEntry = NULL;

static PEB_LDR_DATA_INTERNAL* GetPebLdrData(void)
{
    BYTE* peb;
#ifdef _M_IX86
    peb = (BYTE*)__readfsdword(0x30);
#else
    peb = (BYTE*)__readgsqword(0x60);
#endif
    return *(PEB_LDR_DATA_INTERNAL**)(peb + PEB_LDR_OFFSET);
}

/* LdrLockLoaderLock / LdrUnlockLoaderLock — resolve dynamically from ntdll */
typedef LONG (NTAPI *PFN_LdrLockLoaderLock)(ULONG Flags, ULONG* State, ULONG_PTR* Cookie);
typedef LONG (NTAPI *PFN_LdrUnlockLoaderLock)(ULONG Flags, ULONG_PTR Cookie);

static void HideModuleFromPeb(HMODULE hModule)
{
    PEB_LDR_DATA_INTERNAL* ldr;
    LIST_ENTRY* head;
    LIST_ENTRY* entry;
    ULONG_PTR loaderCookie = 0;
    PFN_LdrLockLoaderLock pfnLock = NULL;
    PFN_LdrUnlockLoaderLock pfnUnlock = NULL;
    HMODULE hNtdll;

    if (g_moduleHidden) return;

    /* M1: Acquire loader lock to prevent concurrent list modification */
    hNtdll = GetModuleHandleW(L"ntdll.dll");
    if (hNtdll) {
        pfnLock = (PFN_LdrLockLoaderLock)GetProcAddress(hNtdll, "LdrLockLoaderLock");
        pfnUnlock = (PFN_LdrUnlockLoaderLock)GetProcAddress(hNtdll, "LdrUnlockLoaderLock");
    }
    if (pfnLock) pfnLock(0, NULL, &loaderCookie);

    ldr = GetPebLdrData();
    if (!ldr) {
        if (pfnUnlock && loaderCookie) pfnUnlock(0, loaderCookie);
        return;
    }

    /* Walk InLoadOrderModuleList to find our entry */
    head = &ldr->InLoadOrderModuleList;
    for (entry = head->Flink; entry != head; entry = entry->Flink)
    {
        LDR_DATA_TABLE_ENTRY_INTERNAL* mod =
            (LDR_DATA_TABLE_ENTRY_INTERNAL*)entry;

        if (mod->DllBase == (PVOID)hModule)
        {
            g_hiddenEntry = mod;

            /* Save links for re-linking */
            g_savedLoadOrderFlink = mod->InLoadOrderLinks.Flink;
            g_savedLoadOrderBlink = mod->InLoadOrderLinks.Blink;
            g_savedMemOrderFlink  = mod->InMemoryOrderLinks.Flink;
            g_savedMemOrderBlink  = mod->InMemoryOrderLinks.Blink;
            g_savedInitOrderFlink = mod->InInitializationOrderLinks.Flink;
            g_savedInitOrderBlink = mod->InInitializationOrderLinks.Blink;

            /* Unlink from all three lists */
            mod->InLoadOrderLinks.Blink->Flink = mod->InLoadOrderLinks.Flink;
            mod->InLoadOrderLinks.Flink->Blink = mod->InLoadOrderLinks.Blink;

            mod->InMemoryOrderLinks.Blink->Flink = mod->InMemoryOrderLinks.Flink;
            mod->InMemoryOrderLinks.Flink->Blink = mod->InMemoryOrderLinks.Blink;

            mod->InInitializationOrderLinks.Blink->Flink = mod->InInitializationOrderLinks.Flink;
            mod->InInitializationOrderLinks.Flink->Blink = mod->InInitializationOrderLinks.Blink;

            g_moduleHidden = TRUE;
            if (pfnUnlock && loaderCookie) pfnUnlock(0, loaderCookie);
            return;
        }
    }

    if (pfnUnlock && loaderCookie) pfnUnlock(0, loaderCookie);
}

static void UnhideModuleInPeb(void)
{
    ULONG_PTR loaderCookie = 0;
    PFN_LdrLockLoaderLock pfnLock = NULL;
    PFN_LdrUnlockLoaderLock pfnUnlock = NULL;
    HMODULE hNtdll;

    if (!g_moduleHidden || !g_hiddenEntry) return;

    /* M1: Acquire loader lock for safe re-linking */
    hNtdll = GetModuleHandleW(L"ntdll.dll");
    if (hNtdll) {
        pfnLock = (PFN_LdrLockLoaderLock)GetProcAddress(hNtdll, "LdrLockLoaderLock");
        pfnUnlock = (PFN_LdrUnlockLoaderLock)GetProcAddress(hNtdll, "LdrUnlockLoaderLock");
    }
    if (pfnLock) pfnLock(0, NULL, &loaderCookie);

    /* Re-link into all three lists */
    g_hiddenEntry->InLoadOrderLinks.Flink = g_savedLoadOrderFlink;
    g_hiddenEntry->InLoadOrderLinks.Blink = g_savedLoadOrderBlink;
    g_savedLoadOrderBlink->Flink = &g_hiddenEntry->InLoadOrderLinks;
    g_savedLoadOrderFlink->Blink = &g_hiddenEntry->InLoadOrderLinks;

    g_hiddenEntry->InMemoryOrderLinks.Flink = g_savedMemOrderFlink;
    g_hiddenEntry->InMemoryOrderLinks.Blink = g_savedMemOrderBlink;
    g_savedMemOrderBlink->Flink = &g_hiddenEntry->InMemoryOrderLinks;
    g_savedMemOrderFlink->Blink = &g_hiddenEntry->InMemoryOrderLinks;

    g_hiddenEntry->InInitializationOrderLinks.Flink = g_savedInitOrderFlink;
    g_hiddenEntry->InInitializationOrderLinks.Blink = g_savedInitOrderBlink;
    g_savedInitOrderBlink->Flink = &g_hiddenEntry->InInitializationOrderLinks;
    g_savedInitOrderFlink->Blink = &g_hiddenEntry->InInitializationOrderLinks;

    g_moduleHidden = FALSE;
    g_hiddenEntry = NULL;

    if (pfnUnlock && loaderCookie) pfnUnlock(0, loaderCookie);
}

/* ── Stealth: Master enable/disable ── */

static HMODULE g_agentModule = NULL; /* set in DllMain */

static BOOL EnableStealth(void)
{
    if (g_stealthActive) return TRUE;

    /* Install NtGetThreadContext hook to cloak DR registers */
    if (!InstallNtGetThreadContextHook()) return FALSE;

    /* Hide our DLL from module enumeration */
    if (g_agentModule)
        HideModuleFromPeb(g_agentModule);

    g_stealthActive = TRUE;
    return TRUE;
}

static BOOL DisableStealth(void)
{
    if (!g_stealthActive) return TRUE;

    /* Re-link module in PEB (must happen before FreeLibrary) */
    UnhideModuleInPeb();

    /* Remove NtGetThreadContext hook */
    RemoveNtGetThreadContextHook();

    g_stealthActive = FALSE;
    return TRUE;
}

/* ── Agent initialization (runs on dedicated thread, NOT under loader lock) ── */

static DWORD WINAPI AgentInitThread(LPVOID param)
{
    char shmName[128], cmdName[128], hitName[128];
    DWORD pid = GetCurrentProcessId();
    DWORD shmTotalSize;

    (void)param;

    _snprintf(shmName, sizeof(shmName), "Local\\CEAISuite_VEH_%u", (unsigned)pid);
    _snprintf(cmdName, sizeof(cmdName), "Local\\CEAISuite_VEH_Cmd_%u", (unsigned)pid);
    _snprintf(hitName, sizeof(hitName), "Local\\CEAISuite_VEH_Hit_%u", (unsigned)pid);

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
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        g_agentModule = hModule;
        /* Minimize work under loader lock — only create the init thread.
         * All SHM mapping, event opening, and VEH registration happens on the
         * init thread, outside the loader lock, avoiding potential deadlocks. */
        g_running = TRUE;
        g_cmdThread = CreateThread(NULL, 0, AgentInitThread, NULL, 0, NULL);
        if (!g_cmdThread) return FALSE;
    }
    else if (reason == DLL_PROCESS_DETACH) {
        DWORD i;

        /* M3 fix: Stop command thread FIRST to prevent race with DisableStealth.
         * Set g_running = FALSE and wait for it to exit before cleanup. */
        g_running = FALSE;
        if (g_cmdThread) {
            WaitForSingleObject(g_cmdThread, 2000);
            CloseHandle(g_cmdThread);
            g_cmdThread = NULL;
        }

        /* Now safe to disable stealth — command thread is stopped */
        if (g_stealthActive) DisableStealth();

        if (g_vehHandle) {
            RemoveVectoredExceptionHandler(g_vehHandle);
            g_vehHandle = NULL;
        }

        /* Remove all hardware breakpoints */
        for (i = 0; i < 4; i++)
            RemoveHardwareBp(i);

        if (g_shm)
            InterlockedExchange(&g_shm->agentStatus, STATUS_SHUTDOWN);

        if (g_shm) { UnmapViewOfFile(g_shm); g_shm = NULL; }
        if (g_shmHandle) { CloseHandle(g_shmHandle); g_shmHandle = NULL; }
        if (g_cmdEvent) { CloseHandle(g_cmdEvent); g_cmdEvent = NULL; }
        if (g_hitEvent) { CloseHandle(g_hitEvent); g_hitEvent = NULL; }
    }

    return TRUE;
}

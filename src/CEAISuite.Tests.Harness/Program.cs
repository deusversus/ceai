using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Track spawned loop threads so STOP_LOOP can cancel them
var loopThreads = new Dictionary<int, (Thread Thread, CancellationTokenSource Cts)>();

// Background thread to produce CPU time so watchdog CPU-usage signal passes
var cpuCts = new CancellationTokenSource();
var cpuThread = new Thread(() =>
{
    while (!cpuCts.Token.IsCancellationRequested)
    {
        Thread.SpinWait(100_000);
        Thread.Sleep(0);
    }
}) { IsBackground = true, Name = "HarnessCpuBackground" };
cpuThread.Start();

// Main command loop
string? line;
while ((line = Console.ReadLine()) != null)
{
    ProcessCommand(line.Trim(), cpuCts, loopThreads);
}

static void ProcessCommand(
    string line,
    CancellationTokenSource cpuCts,
    Dictionary<int, (Thread Thread, CancellationTokenSource Cts)> loopThreads)
{
    if (string.IsNullOrEmpty(line)) return;

    var parts = line.Split(' ', 3);
    var cmd = parts[0].ToUpperInvariant();

    switch (cmd)
    {
        case "READY":
            Respond($"PID:{Environment.ProcessId}");
            break;

        case "PING":
            Respond("PONG");
            break;

        case "ALLOC":
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], out var size))
            {
                Respond("ALLOC_ERR:invalid_size");
                break;
            }

            nint addr = NativeMethods.VirtualAlloc(
                IntPtr.Zero, (nuint)size,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_EXECUTE_READWRITE);

            if (addr == IntPtr.Zero)
            {
                Respond("ALLOC_ERR:virtualalloc_failed");
                break;
            }

            var fillBuf = new byte[size];
            Array.Fill(fillBuf, (byte)0xDE);
            Marshal.Copy(fillBuf, 0, addr, size);

            Respond($"ALLOC_OK:{addr:X}");
            break;
        }

        case "THREADS":
        {
            var threads = Process.GetCurrentProcess().Threads;
            var ids = new List<string>();
            foreach (ProcessThread t in threads)
                ids.Add(t.Id.ToString(CultureInfo.InvariantCulture));
            Respond($"THREADS:{string.Join(",", ids)}");
            break;
        }

        case "MODULE_INFO":
        {
            var mainModule = Process.GetCurrentProcess().MainModule;
            if (mainModule is null)
            {
                Respond("MODULE_INFO_ERR:no_main_module");
                break;
            }
            var baseAddr = (nuint)(ulong)mainModule.BaseAddress;
            Respond($"MODULE_INFO:{baseAddr:X}:{mainModule.ModuleMemorySize}");
            break;
        }

        case "WRITE_AT":
        {
            // WRITE_AT <address_hex> <hex_bytes>
            if (parts.Length < 3)
            {
                Respond("WRITE_AT_ERR:usage_WRITE_AT_addr_hexbytes");
                break;
            }

            if (!TryParseHexAddress(parts[1], out var addr))
            {
                Respond("WRITE_AT_ERR:invalid_address");
                break;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromHexString(parts[2].Replace(" ", "", StringComparison.Ordinal));
            }
            catch
            {
                Respond("WRITE_AT_ERR:invalid_hex_bytes");
                break;
            }

            Marshal.Copy(bytes, 0, (IntPtr)addr, bytes.Length);
            Respond("WRITE_AT_OK");
            break;
        }

        case "WRITE_LOOP":
        {
            // WRITE_LOOP <address_hex> <interval_ms>
            if (parts.Length < 3 || !TryParseHexAddress(parts[1], out var addr)
                || !int.TryParse(parts[2], out var intervalMs))
            {
                Respond("WRITE_LOOP_ERR:usage_WRITE_LOOP_addr_interval");
                break;
            }

            var cts = new CancellationTokenSource();
            var tidReady = new ManualResetEventSlim(false);
            int nativeTid = 0;

            var thread = new Thread(() =>
            {
                nativeTid = (int)NativeMethods.GetCurrentThreadId();
                tidReady.Set();

                int counter = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        Marshal.WriteInt32((IntPtr)addr, counter++);
                    }
                    catch { break; }
                    Thread.Sleep(intervalMs);
                }
            }) { IsBackground = true, Name = $"WriteLoop-{addr:X}" };

            thread.Start();
            tidReady.Wait(TimeSpan.FromSeconds(3));
            loopThreads[nativeTid] = (thread, cts);
            Respond($"WRITE_LOOP_OK:{nativeTid}");
            break;
        }

        case "EXEC_LOOP":
        {
            // EXEC_LOOP <address_hex> <interval_ms>
            if (parts.Length < 3 || !TryParseHexAddress(parts[1], out var addr)
                || !int.TryParse(parts[2], out var intervalMs))
            {
                Respond("EXEC_LOOP_ERR:usage_EXEC_LOOP_addr_interval");
                break;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<Action>((IntPtr)addr);
            var cts = new CancellationTokenSource();
            var tidReady = new ManualResetEventSlim(false);
            int nativeTid = 0;

            var thread = new Thread(() =>
            {
                nativeTid = (int)NativeMethods.GetCurrentThreadId();
                tidReady.Set();

                while (!cts.Token.IsCancellationRequested)
                {
                    try { fn(); }
                    catch { break; }
                    Thread.Sleep(intervalMs);
                }
            }) { IsBackground = true, Name = $"ExecLoop-{addr:X}" };

            thread.Start();
            tidReady.Wait(TimeSpan.FromSeconds(3));
            loopThreads[nativeTid] = (thread, cts);
            Respond($"EXEC_LOOP_OK:{nativeTid}");
            break;
        }

        case "CALL_FUNC":
        {
            // CALL_FUNC <address_hex>
            if (parts.Length < 2 || !TryParseHexAddress(parts[1], out var addr))
            {
                Respond("CALL_FUNC_ERR:invalid_address");
                break;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<Action>((IntPtr)addr);
            var tidReady = new ManualResetEventSlim(false);
            int nativeTid = 0;

            var thread = new Thread(() =>
            {
                nativeTid = (int)NativeMethods.GetCurrentThreadId();
                tidReady.Set();
                try { fn(); }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[Harness] CALL_FUNC exception: {ex.GetType().Name}: {ex.Message}");
                }
            }) { IsBackground = true, Name = $"CallFunc-{addr:X}" };

            thread.Start();
            tidReady.Wait(TimeSpan.FromSeconds(3));
            thread.Join(TimeSpan.FromSeconds(5));
            Respond($"CALL_FUNC_OK:{nativeTid}");
            break;
        }

        case "STOP_LOOP":
        {
            // STOP_LOOP <native_thread_id>
            if (parts.Length < 2 || !int.TryParse(parts[1], out var tid))
            {
                Respond("STOP_LOOP_ERR:invalid_thread_id");
                break;
            }

            if (loopThreads.TryGetValue(tid, out var entry))
            {
                entry.Cts.Cancel();
                entry.Thread.Join(TimeSpan.FromSeconds(3));
                loopThreads.Remove(tid);
                Respond("STOP_LOOP_OK");
            }
            else
            {
                Respond("STOP_LOOP_ERR:not_found");
            }
            break;
        }

        case "NEST_CALLS":
        {
            // NEST_CALLS <depth>
            if (parts.Length < 2 || !int.TryParse(parts[1], out var depth) || depth < 1 || depth > 100)
            {
                Respond("NEST_CALLS_ERR:invalid_depth");
                break;
            }

            var tidReady = new ManualResetEventSlim(false);
            int nativeTid = 0;

            var thread = new Thread(() =>
            {
                nativeTid = (int)NativeMethods.GetCurrentThreadId();
                tidReady.Set();
                NestHelper.CallNested(depth, 0);
            }) { IsBackground = true, Name = $"NestCalls-{depth}" };

            thread.Start();
            tidReady.Wait(TimeSpan.FromSeconds(3));
            // Don't join — the deepest frame sleeps forever
            loopThreads[nativeTid] = (thread, new CancellationTokenSource());
            Respond($"NEST_CALLS_OK:{nativeTid}:0");
            break;
        }

        case "SPIN":
            cpuCts.Cancel();
            Thread.Sleep(100);
            while (true) { Thread.SpinWait(10000); }

        case "BLOCK":
            cpuCts.Cancel();
            Thread.Sleep(100);
            new ManualResetEventSlim(false).Wait();
            break;

        case "DEADLOCK":
        {
            var mutexA = new Mutex(false, null);
            var mutexB = new Mutex(false, null);
            var barrier = new ManualResetEventSlim(false);

            var threadA = new Thread(() =>
            {
                mutexA.WaitOne();
                barrier.Wait();
                mutexB.WaitOne();
            }) { Name = "DeadlockThreadA", IsBackground = true };

            var threadB = new Thread(() =>
            {
                mutexB.WaitOne();
                barrier.Wait();
                mutexA.WaitOne();
            }) { Name = "DeadlockThreadB", IsBackground = true };

            threadA.Start();
            threadB.Start();
            Thread.Sleep(100);
            barrier.Set();

            Respond("DEADLOCK_OK");
            break;
        }

        case "EXIT":
        {
            var code = parts.Length >= 2 && int.TryParse(parts[1], out var c) ? c : 0;
            Environment.Exit(code);
            break;
        }

        default:
            Respond($"UNKNOWN:{cmd}");
            break;
    }
}

static void Respond(string message)
{
    Console.WriteLine(message);
    Console.Out.Flush();
}

static bool TryParseHexAddress(string text, out nuint address)
{
    address = 0;
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        text = text[2..];
    return nuint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
}

/// <summary>
/// Helper for NEST_CALLS: creates a known call stack N frames deep.
/// Each method is [NoInlining] to ensure the CLR preserves the frame on the stack.
/// The deepest frame sleeps forever so the stack can be walked at any time.
/// </summary>
static class NestHelper
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CallNested(int totalDepth, int currentDepth)
    {
        if (currentDepth >= totalDepth - 1)
        {
            // Deepest frame — sleep forever so stack can be walked
            Thread.Sleep(Timeout.Infinite);
            return;
        }
        CallNested(totalDepth, currentDepth + 1);
    }
}

static class NativeMethods
{
    public const uint MEM_COMMIT  = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint VirtualAlloc(
        IntPtr lpAddress,
        nuint  dwSize,
        uint   flAllocationType,
        uint   flProtect);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}

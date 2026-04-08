using System.Runtime.InteropServices;

// Background thread to produce CPU time so watchdog CPU-usage signal passes
// Cancellation token to stop CPU thread when process becomes "unresponsive"
var cpuCts = new CancellationTokenSource();

// Background thread that actively burns CPU so the watchdog's Signal 3
// (CPU time progress check with 50ms sample window) reliably passes.
var cpuThread = new Thread(() =>
{
    while (!cpuCts.Token.IsCancellationRequested)
    {
        // SpinWait burns CPU without yielding, ensuring TotalProcessorTime advances
        Thread.SpinWait(100_000);
        Thread.Sleep(0); // Yield occasionally to avoid starving the system
    }
}) { IsBackground = true, Name = "HarnessCpuBackground" };
cpuThread.Start();

// Main command loop
string? line;
while ((line = Console.ReadLine()) != null)
{
    ProcessCommand(line.Trim(), cpuCts);
}

static void ProcessCommand(string line, CancellationTokenSource cpuCts)
{
    if (string.IsNullOrEmpty(line)) return;

    var parts = line.Split(' ', 2);
    var cmd = parts[0].ToUpperInvariant();

    switch (cmd)
    {
        case "READY":
            Console.WriteLine($"PID:{Environment.ProcessId}");
            Console.Out.Flush();
            break;

        case "PING":
            Console.WriteLine("PONG");
            Console.Out.Flush();
            break;

        case "ALLOC":
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], out var size))
            {
                Console.WriteLine("ALLOC_ERR:invalid_size");
                Console.Out.Flush();
                break;
            }

            nint addr = NativeMethods.VirtualAlloc(
                IntPtr.Zero,
                (nuint)size,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_EXECUTE_READWRITE);

            if (addr == IntPtr.Zero)
            {
                Console.WriteLine("ALLOC_ERR:virtualalloc_failed");
                Console.Out.Flush();
                break;
            }

            // Fill with 0xDE pattern
            var fillBuf = new byte[size];
            Array.Fill(fillBuf, (byte)0xDE);
            Marshal.Copy(fillBuf, 0, addr, size);

            Console.WriteLine($"ALLOC_OK:{addr:X}");
            Console.Out.Flush();
            break;
        }

        case "SPIN":
            // Stop CPU background thread so Signal 3 (CPU progress) fails
            cpuCts.Cancel();
            Thread.Sleep(100); // Let it stop
            // Spin on current thread — never returns, stdin no longer processed
            while (true)
            {
                Thread.SpinWait(10000);
            }

        case "BLOCK":
            // Stop CPU background thread so Signal 3 (CPU progress) fails
            cpuCts.Cancel();
            Thread.Sleep(100); // Let it stop
            // Block main thread indefinitely — process appears fully hung
            new ManualResetEventSlim(false).Wait();
            break;

        case "DEADLOCK":
        {
            // Use kernel Mutex objects — WCT can detect these (unlike Monitor.Enter)
            var mutexA = new Mutex(false, null);
            var mutexB = new Mutex(false, null);
            var barrier = new ManualResetEventSlim(false);

            var threadA = new Thread(() =>
            {
                mutexA.WaitOne();
                barrier.Wait(); // Ensure both threads hold their first mutex
                mutexB.WaitOne(); // Block — threadB holds mutexB
            }) { Name = "DeadlockThreadA", IsBackground = true };

            var threadB = new Thread(() =>
            {
                mutexB.WaitOne();
                barrier.Wait(); // Ensure both threads hold their first mutex
                mutexA.WaitOne(); // Block — threadA holds mutexA
            }) { Name = "DeadlockThreadB", IsBackground = true };

            threadA.Start();
            threadB.Start();
            Thread.Sleep(100); // Let both threads acquire their first mutex
            barrier.Set(); // Release barrier so both try to acquire the other's mutex

            Console.WriteLine("DEADLOCK_OK");
            Console.Out.Flush();
            break;
        }

        case "EXIT":
        {
            var code = parts.Length >= 2 && int.TryParse(parts[1], out var c) ? c : 0;
            Environment.Exit(code);
            break;
        }

        default:
            Console.WriteLine($"UNKNOWN:{cmd}");
            Console.Out.Flush();
            break;
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
}

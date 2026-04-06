using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

public sealed class WindowsEngineFacade : IEngineFacade
{
    /// <summary>Optional trace callback for diagnosing attach/detach issues.
    /// Set from the host (e.g. MainViewModel) to route engine diagnostics to the Output panel.</summary>
    public Action<string>? DiagnosticTrace { get; set; }

    private void Trace(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[EngineFacade] {message}");
        DiagnosticTrace?.Invoke(message);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;

    private readonly object _attachLock = new();
    private int? _attachedProcessId;

    // 4A: Cached process handle per attached process — avoids re-opening per operation
    private IntPtr _cachedProcessHandle;
    // 4D: Cached architecture per attached process
    private string? _cachedArchitecture;
    // Cached attachment result — avoids redundant module enumeration when
    // AttachAsync is called twice for the same PID (InspectProcess + AddressTable).
    private EngineAttachment? _cachedAttachment;

    public int? AttachedProcessId
    {
        get { lock (_attachLock) return _attachedProcessId; }
    }

    public bool IsAttached
    {
        get { lock (_attachLock) return _attachedProcessId.HasValue; }
    }

    public IReadOnlyCollection<EngineCapability> Capabilities { get; } =
        new[]
        {
            EngineCapability.ProcessEnumeration,
            EngineCapability.MemoryRead,
            EngineCapability.MemoryWrite,
            EngineCapability.Disassembly,
            EngineCapability.SessionPersistence
        };

    public Task<IReadOnlyList<ProcessDescriptor>> ListProcessesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ProcessDescriptor>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var results = new List<ProcessDescriptor>();
                var processes = Process.GetProcesses();
                try
                {
                    foreach (var process in processes)
                    {
                        try
                        {
                            results.Add(CreateDescriptor(process));
                        }
                        catch
                        {
                            // Process exited between enumeration and descriptor creation — skip it.
                        }
                    }
                }
                finally
                {
                    foreach (var process in processes)
                    {
                        try { process.Dispose(); } catch { }
                    }
                }

                results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return results;
            },
            cancellationToken);

    public Task<EngineAttachment> AttachAsync(int processId, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Trace($"AttachAsync called for PID {processId}. Current state: _attachedProcessId={_attachedProcessId}, handleValid={_cachedProcessHandle != IntPtr.Zero}, hasCachedAttachment={_cachedAttachment is not null}");

                // Return cached attachment if we already successfully attached to this PID.
                // This avoids redundant module enumeration when AttachAsync is called
                // twice in quick succession (once from InspectProcess, once for AddressTable).
                lock (_attachLock)
                {
                    if (_cachedAttachment is not null && _cachedAttachment.ProcessId == processId)
                    {
                        Trace($"AttachAsync: returning cached attachment for PID {processId} ({_cachedAttachment.Modules.Count} modules).");
                        return _cachedAttachment;
                    }
                    if (_cachedAttachment is not null)
                        Trace($"AttachAsync: cached attachment is for different PID {_cachedAttachment.ProcessId}, re-attaching to {processId}.");
                }

                // Retry module enumeration with backoff — the target process may still
                // be loading DLLs (loader lock held), especially when the game is
                // launched after CEAI is already running.
                const int maxAttempts = 4;
                Exception? lastException = null;

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Trace($"AttachAsync: attempt {attempt}/{maxAttempts} for PID {processId}...");

                    try
                    {
                        using var process = Process.GetProcessById(processId);
                        Trace($"AttachAsync: Process.GetProcessById succeeded — {process.ProcessName}, HasExited={process.HasExited}");

                        // Verify the process handle is usable before enumerating modules.
                        // OpenProcess with QUERY_LIMITED can succeed even when VM_READ
                        // would fail (e.g. during process init or anticheat hooks).
                        var testHandle = OpenProcess(CachedHandleAccess, false, process.Id);
                        if (testHandle == IntPtr.Zero)
                        {
                            var errorCode = Marshal.GetLastWin32Error();
                            Trace($"AttachAsync: OpenProcess failed with error {errorCode} (access=0x{CachedHandleAccess:X})");
                            throw new Win32Exception(errorCode,
                                $"Cannot open process with required access (error {errorCode}). " +
                                "The process may still be initializing or is protected.");
                        }
                        Trace($"AttachAsync: OpenProcess succeeded, handle=0x{testHandle:X}");

                        // Handle is good — cache it now so we don't re-open later
                        lock (_attachLock)
                        {
                            _attachedProcessId = process.Id;
                            if (_cachedProcessHandle != IntPtr.Zero)
                            {
                                Trace($"AttachAsync: closing old cached handle 0x{_cachedProcessHandle:X}");
                                CloseHandle(_cachedProcessHandle);
                            }
                            _cachedProcessHandle = testHandle;
                        }

                        Trace("AttachAsync: enumerating modules...");
                        var modules = process.Modules
                            .Cast<ProcessModule>()
                            .Select(
                                module => new ModuleDescriptor(
                                    module.ModuleName,
                                    unchecked((nuint)module.BaseAddress.ToInt64()),
                                    module.ModuleMemorySize))
                            .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        Trace($"AttachAsync: enumerated {modules.Length} modules successfully.");

                        var attachment = new EngineAttachment(process.Id, process.ProcessName, modules);
                        lock (_attachLock)
                        {
                            _cachedAttachment = attachment;
                        }
                        Trace($"AttachAsync: SUCCESS — attached to {process.ProcessName} (PID {process.Id}), {modules.Length} modules cached.");
                        return attachment;
                    }
                    catch (Win32Exception exception) when (attempt < maxAttempts)
                    {
                        lastException = exception;
                        Trace($"AttachAsync: attempt {attempt}/{maxAttempts} Win32Exception: {exception.Message} (NativeErrorCode={exception.NativeErrorCode}). Retrying...");
                        // Backoff: 250ms, 500ms, 1000ms
                        Thread.Sleep(250 * (1 << (attempt - 1)));
                    }
                    catch (Win32Exception exception)
                    {
                        lastException = exception;
                        Trace($"AttachAsync: FINAL attempt {attempt}/{maxAttempts} Win32Exception: {exception.Message} (NativeErrorCode={exception.NativeErrorCode}). No more retries.");
                    }
                    catch (InvalidOperationException ex) when (attempt < maxAttempts)
                    {
                        // Process.GetProcessById can throw if process exited between retries
                        lastException = ex;
                        Trace($"AttachAsync: attempt {attempt}/{maxAttempts} InvalidOperationException: {ex.Message}. Retrying...");
                        Thread.Sleep(250 * (1 << (attempt - 1)));
                    }
                    catch (InvalidOperationException ex)
                    {
                        lastException = ex;
                        Trace($"AttachAsync: FINAL attempt {attempt}/{maxAttempts} InvalidOperationException: {ex.Message}. No more retries.");
                    }
                }

                // All retries exhausted — clean up partial attach state
                Trace($"AttachAsync: ALL {maxAttempts} ATTEMPTS EXHAUSTED for PID {processId}. Cleaning up partial state...");
                lock (_attachLock)
                {
                    if (_attachedProcessId == processId)
                    {
                        _attachedProcessId = null;
                        if (_cachedProcessHandle != IntPtr.Zero)
                        {
                            CloseHandle(_cachedProcessHandle);
                            _cachedProcessHandle = IntPtr.Zero;
                        }
                        _cachedAttachment = null;
                        Trace("AttachAsync: partial state cleaned up (_attachedProcessId=null, handle closed, cache cleared).");
                    }
                    else
                    {
                        Trace($"AttachAsync: skipped cleanup — _attachedProcessId is {_attachedProcessId}, not {processId}.");
                    }
                }

                throw new InvalidOperationException(
                    $"Unable to attach to process {processId} after {maxAttempts} attempts. " +
                    "The process may still be initializing — try again in a few seconds.",
                    lastException);
            },
            cancellationToken);

    public void Detach()
    {
        lock (_attachLock)
        {
            Trace($"Detach: clearing state. _attachedProcessId={_attachedProcessId}, handleValid={_cachedProcessHandle != IntPtr.Zero}, hasCachedAttachment={_cachedAttachment is not null}");
            _attachedProcessId = null;
            // 4A: Close cached process handle on detach
            if (_cachedProcessHandle != IntPtr.Zero)
            {
                CloseHandle(_cachedProcessHandle);
                _cachedProcessHandle = IntPtr.Zero;
            }
            // 4D: Invalidate cached architecture
            _cachedArchitecture = null;
            _cachedAttachment = null;
            Trace("Detach: all state cleared.");
        }
    }

    // Comprehensive access mask covering all operations (read, write, query).
    // The cached handle is opened once with full permissions so that a handle
    // first opened for ReadMemoryAsync (VM_READ) is equally valid for
    // WriteBytesAsync (VM_WRITE | VM_OPERATION) without re-opening.
    private const uint CachedHandleAccess =
        ProcessQueryLimitedInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation; // 0x1038

    // 4A: Get or open a cached process handle for the attached process
    private IntPtr GetOrOpenProcessHandle(int processId, uint access)
    {
        lock (_attachLock)
        {
            if (_attachedProcessId == processId && _cachedProcessHandle != IntPtr.Zero)
                return _cachedProcessHandle;
        }

        // For the attached process, open with comprehensive access so the cached
        // handle works for every operation type. For non-attached processes use
        // exactly the requested access.
        var effectiveAccess = false;
        lock (_attachLock) { effectiveAccess = _attachedProcessId == processId; }
        var handle = OpenProcess(effectiveAccess ? CachedHandleAccess : access, false, processId);
        if (handle == IntPtr.Zero)
            return IntPtr.Zero;

        lock (_attachLock)
        {
            if (_attachedProcessId == processId)
            {
                // Cache it for the attached process
                if (_cachedProcessHandle != IntPtr.Zero)
                    CloseHandle(_cachedProcessHandle);
                _cachedProcessHandle = handle;
                return handle;
            }
        }

        // Not attached to this process — caller must close
        return handle;
    }

    private bool ShouldCloseHandle(int processId)
    {
        lock (_attachLock) { return _attachedProcessId != processId; }
    }

    public Task<MemoryReadResult> ReadMemoryAsync(
        int processId,
        nuint address,
        int length,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
                if (length > 0x10000)
                {
                    throw new ArgumentOutOfRangeException(nameof(length), "Reads are limited to 65536 bytes per call.");
                }

                var handle = GetOrOpenProcessHandle(processId, ProcessQueryLimitedInformation | ProcessVmRead);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unable to open process {processId} for memory reads.");
                }

                var closeHandle = ShouldCloseHandle(processId);
                try
                {
                    var buffer = new byte[length];
                    if (!ReadProcessMemory(handle, (IntPtr)address, buffer, length, out var bytesRead) || bytesRead <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Unable to read memory at 0x{address:X} from process {processId}.");
                    }

                    // 4B: Detect partial reads and flag them
                    var isPartial = bytesRead < length;
                    if (isPartial)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[EngineFacade] Partial read at 0x{address:X}: got {bytesRead}/{length} bytes.");
                    }

                    return new MemoryReadResult(processId, address, buffer[..bytesRead], isPartial);
                }
                finally
                {
                    if (closeHandle) CloseHandle(handle);
                }
            },
            cancellationToken);

    public async Task<TypedMemoryValue> ReadValueAsync(
        int processId,
        nuint address,
        MemoryDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var size = GetReadSize(processId, dataType);
        var read = await ReadMemoryAsync(processId, address, size, cancellationToken);
        var bytes = read.Bytes.ToArray();

        var displayValue = dataType switch
        {
            MemoryDataType.Byte => bytes[0].ToString(),
            MemoryDataType.Int16 => BitConverter.ToInt16(bytes, 0).ToString(),
            MemoryDataType.Int32 => BitConverter.ToInt32(bytes, 0).ToString(),
            MemoryDataType.Int64 => BitConverter.ToInt64(bytes, 0).ToString(),
            MemoryDataType.Float => BitConverter.ToSingle(bytes, 0).ToString("G9"),
            MemoryDataType.Double => BitConverter.ToDouble(bytes, 0).ToString("G17"),
            MemoryDataType.Pointer => FormatPointer(bytes),
            MemoryDataType.String => ExtractNullTerminatedString(bytes),
            MemoryDataType.ByteArray => Convert.ToHexString(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported memory data type.")
        };

        return new TypedMemoryValue(processId, address, dataType, displayValue, bytes);
    }

    public Task<MemoryWriteResult> WriteValueAsync(
        int processId,
        nuint address,
        MemoryDataType dataType,
        string value,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytes = ConvertValueToBytes(processId, dataType, value);
                var handle = GetOrOpenProcessHandle(processId, ProcessQueryLimitedInformation | ProcessVmWrite | ProcessVmOperation);

                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unable to open process {processId} for memory writes.");
                }

                var closeHandle = ShouldCloseHandle(processId);
                try
                {
                    if (!WriteProcessMemory(handle, (IntPtr)address, bytes, bytes.Length, out var bytesWritten) || bytesWritten != bytes.Length)
                    {
                        throw new InvalidOperationException(
                            $"Unable to write memory at 0x{address:X} in process {processId}.");
                    }

                    return new MemoryWriteResult(processId, address, dataType, value, bytesWritten);
                }
                finally
                {
                    if (closeHandle) CloseHandle(handle);
                }
            },
            cancellationToken);

    public Task<int> WriteBytesAsync(
        int processId,
        nuint address,
        byte[] data,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handle = GetOrOpenProcessHandle(processId, ProcessQueryLimitedInformation | ProcessVmWrite | ProcessVmOperation);

                if (handle == IntPtr.Zero)
                    throw new InvalidOperationException($"Unable to open process {processId} for memory writes.");

                var closeHandle = ShouldCloseHandle(processId);
                try
                {
                    if (!WriteProcessMemory(handle, (IntPtr)address, data, data.Length, out var bytesWritten))
                        throw new InvalidOperationException(
                            $"Unable to write {data.Length} bytes at 0x{address:X} in process {processId}.");

                    // 4C: Detect partial writes and throw
                    if (bytesWritten != data.Length)
                        throw new InvalidOperationException(
                            $"Partial write at 0x{address:X}: wrote {bytesWritten}/{data.Length} bytes. Target memory may be in an inconsistent state.");

                    return bytesWritten;
                }
                finally
                {
                    if (closeHandle) CloseHandle(handle);
                }
            },
            cancellationToken);

    private ProcessDescriptor CreateDescriptor(Process process)
    {
        var architecture = TryGetArchitectureCached(process.Id);
        return new ProcessDescriptor(process.Id, process.ProcessName, architecture);
    }

    // 4D: Cache architecture per attached process
    private string TryGetArchitectureCached(int processId)
    {
        lock (_attachLock)
        {
            if (_attachedProcessId == processId && _cachedArchitecture is not null)
                return _cachedArchitecture;
        }

        var arch = TryGetArchitecture(processId);

        lock (_attachLock)
        {
            if (_attachedProcessId == processId)
                _cachedArchitecture = arch;
        }

        return arch;
    }

    private static string TryGetArchitecture(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return "Unknown";
        }

        try
        {
            if (!IsWow64Process2(handle, out var processMachine, out var nativeMachine))
            {
                return "Unknown";
            }

            if (processMachine == 0)
            {
                return nativeMachine switch
                {
                    ImageFileMachineAmd64 => "x64",
                    ImageFileMachineArm64 => "arm64",
                    ImageFileMachineI386 => "x86",
                    _ => "Unknown"
                };
            }

            return processMachine switch
            {
                ImageFileMachineI386 => "x86",
                ImageFileMachineAmd64 => "x64",
                ImageFileMachineArm64 => "arm64",
                _ => "Unknown"
            };
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private int GetReadSize(int processId, MemoryDataType dataType) =>
        dataType switch
        {
            MemoryDataType.Byte => 1,
            MemoryDataType.Int16 => sizeof(short),
            MemoryDataType.Int32 => sizeof(int),
            MemoryDataType.Int64 => sizeof(long),
            MemoryDataType.Float => sizeof(float),
            MemoryDataType.Double => sizeof(double),
            MemoryDataType.Pointer => string.Equals(TryGetArchitectureCached(processId), "x86", StringComparison.OrdinalIgnoreCase)
                ? sizeof(int)
                : sizeof(long),
            MemoryDataType.String => 256, // read up to 256 bytes for null-terminated strings
            MemoryDataType.ByteArray => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported memory data type.")
        };

    private byte[] ConvertValueToBytes(int processId, MemoryDataType dataType, string value) =>
        dataType switch
        {
            MemoryDataType.Byte => [(byte)int.Parse(value, CultureInfo.InvariantCulture)],
            MemoryDataType.Int16 => BitConverter.GetBytes(short.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Int32 => BitConverter.GetBytes(int.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Int64 => BitConverter.GetBytes(long.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Float => BitConverter.GetBytes(float.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Double => BitConverter.GetBytes(double.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Pointer => ConvertPointerValue(processId, value),
            MemoryDataType.String => System.Text.Encoding.UTF8.GetBytes(value + '\0'),
            MemoryDataType.ByteArray => Convert.FromHexString(value.Replace(" ", "")),
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported memory data type.")
        };

    private byte[] ConvertPointerValue(int processId, string value)
    {
        var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        var is32Bit = string.Equals(TryGetArchitectureCached(processId), "x86", StringComparison.OrdinalIgnoreCase);
        return is32Bit
            ? BitConverter.GetBytes(uint.Parse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : BitConverter.GetBytes(ulong.Parse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static string FormatPointer(IReadOnlyList<byte> bytes)
    {
        return bytes.Count switch
        {
            4 => $"0x{BitConverter.ToUInt32(bytes.ToArray(), 0):X8}",
            8 => $"0x{BitConverter.ToUInt64(bytes.ToArray(), 0):X16}",
            _ => $"0x{Convert.ToHexString(bytes.ToArray())}"
        };
    }

    private static string ExtractNullTerminatedString(byte[] bytes)
    {
        var nullIdx = Array.IndexOf(bytes, (byte)0);
        var length = nullIdx >= 0 ? nullIdx : bytes.Length;
        return System.Text.Encoding.UTF8.GetString(bytes, 0, length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out int numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out int numberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr processHandle, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

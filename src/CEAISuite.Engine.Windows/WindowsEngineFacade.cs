using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

public sealed class WindowsEngineFacade : IEngineFacade
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;

    private readonly object _attachLock = new();
    private int? _attachedProcessId;

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

                using var process = Process.GetProcessById(processId);

                try
                {
                    var modules = process.Modules
                        .Cast<ProcessModule>()
                        .Select(
                            module => new ModuleDescriptor(
                                module.ModuleName,
                                unchecked((nuint)module.BaseAddress.ToInt64()),
                                module.ModuleMemorySize))
                        .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    lock (_attachLock)
                    {
                        _attachedProcessId = process.Id;
                    }

                    return new EngineAttachment(process.Id, process.ProcessName, modules);
                }
                catch (Win32Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Unable to enumerate modules for process {process.ProcessName} ({process.Id}).",
                        exception);
                }
            },
            cancellationToken);

    public void Detach()
    {
        lock (_attachLock)
        {
            _attachedProcessId = null;
        }
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

                var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unable to open process {processId} for memory reads.");
                }

                try
                {
                    var buffer = new byte[length];
                    if (!ReadProcessMemory(handle, (IntPtr)address, buffer, length, out var bytesRead) || bytesRead <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Unable to read memory at 0x{address:X} from process {processId}.");
                    }

                    return new MemoryReadResult(processId, address, buffer[..bytesRead]);
                }
                finally
                {
                    CloseHandle(handle);
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
                var handle = OpenProcess(
                    ProcessQueryLimitedInformation | ProcessVmWrite | ProcessVmOperation,
                    false,
                    processId);

                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unable to open process {processId} for memory writes.");
                }

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
                    CloseHandle(handle);
                }
            },
            cancellationToken);

    private static ProcessDescriptor CreateDescriptor(Process process)
    {
        var architecture = TryGetArchitecture(process.Id);
        return new ProcessDescriptor(process.Id, process.ProcessName, architecture);
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

    private static int GetReadSize(int processId, MemoryDataType dataType) =>
        dataType switch
        {
            MemoryDataType.Byte => 1,
            MemoryDataType.Int16 => sizeof(short),
            MemoryDataType.Int32 => sizeof(int),
            MemoryDataType.Int64 => sizeof(long),
            MemoryDataType.Float => sizeof(float),
            MemoryDataType.Double => sizeof(double),
            MemoryDataType.Pointer => string.Equals(TryGetArchitecture(processId), "x86", StringComparison.OrdinalIgnoreCase)
                ? sizeof(int)
                : sizeof(long),
            MemoryDataType.String => 256, // read up to 256 bytes for null-terminated strings
            MemoryDataType.ByteArray => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported memory data type.")
        };

    private static byte[] ConvertValueToBytes(int processId, MemoryDataType dataType, string value) =>
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

    private static byte[] ConvertPointerValue(int processId, string value)
    {
        var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        var is32Bit = string.Equals(TryGetArchitecture(processId), "x86", StringComparison.OrdinalIgnoreCase);
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

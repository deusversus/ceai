namespace CEAISuite.Engine.Abstractions;

public enum EngineCapability
{
    ProcessEnumeration,
    MemoryRead,
    MemoryWrite,
    ValueScanning,
    Disassembly,
    BreakpointTracing,
    SessionPersistence
}

public enum MemoryDataType
{
    Byte,
    Int16,
    Int32,
    Int64,
    Float,
    Double,
    Pointer,
    String,
    ByteArray
}

public sealed record ProcessDescriptor(int Id, string Name, string Architecture);

public sealed record ModuleDescriptor(string Name, nuint BaseAddress, long SizeBytes);

public sealed record EngineAttachment(
    int ProcessId,
    string ProcessName,
    IReadOnlyList<ModuleDescriptor> Modules);

public sealed record MemoryReadResult(
    int ProcessId,
    nuint Address,
    IReadOnlyList<byte> Bytes);

public sealed record TypedMemoryValue(
    int ProcessId,
    nuint Address,
    MemoryDataType DataType,
    string DisplayValue,
    IReadOnlyList<byte> RawBytes);

public sealed record MemoryWriteResult(
    int ProcessId,
    nuint Address,
    MemoryDataType DataType,
    string WrittenValue,
    int BytesWritten);

public interface IEngineFacade
{
    IReadOnlyCollection<EngineCapability> Capabilities { get; }

    /// <summary>The PID of the currently attached process, or null if not attached.</summary>
    int? AttachedProcessId { get; }

    /// <summary>True if currently attached to a process.</summary>
    bool IsAttached { get; }

    Task<IReadOnlyList<ProcessDescriptor>> ListProcessesAsync(CancellationToken cancellationToken = default);

    Task<EngineAttachment> AttachAsync(int processId, CancellationToken cancellationToken = default);

    /// <summary>Detach from the current process, releasing any cached state.</summary>
    void Detach();

    Task<MemoryReadResult> ReadMemoryAsync(
        int processId,
        nuint address,
        int length,
        CancellationToken cancellationToken = default);

    Task<TypedMemoryValue> ReadValueAsync(
        int processId,
        nuint address,
        MemoryDataType dataType,
        CancellationToken cancellationToken = default);

    Task<MemoryWriteResult> WriteValueAsync(
        int processId,
        nuint address,
        MemoryDataType dataType,
        string value,
        CancellationToken cancellationToken = default);
}

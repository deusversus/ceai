using System.ComponentModel;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

/// <summary>
/// Wraps StubEngineFacade with a fault injection dictionary.
/// When a method name matches a key in FaultMap, throws the configured exception
/// instead of delegating to the inner facade.
/// </summary>
public sealed class FaultInjectingEngineFacade : IEngineFacade
{
    private readonly StubEngineFacade _inner = new();
    public Dictionary<string, Exception> FaultMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Delegate all IEngineFacade properties to _inner
    public int? AttachedProcessId => _inner.AttachedProcessId;
    public bool IsAttached => _inner.IsAttached;
    public IReadOnlyCollection<EngineCapability> Capabilities => _inner.Capabilities;

    // Convenience methods
    public void InjectFault(string methodName, Exception ex) => FaultMap[methodName] = ex;
    public void InjectAccessDenied(string methodName) => FaultMap[methodName] = new Win32Exception(5, "Access denied");
    public void InjectPartialCopy(string methodName) => FaultMap[methodName] = new Win32Exception(299, "Partial copy");
    public void InjectProcessTerminating(string methodName) => FaultMap[methodName] = new InvalidOperationException("Process is terminating");
    public void ClearFaults() => FaultMap.Clear();
    public void WriteMemoryDirect(nuint address, byte[] data) => _inner.WriteMemoryDirect(address, data);
    public ModuleDescriptor[] AttachModules { get => _inner.AttachModules; set => _inner.AttachModules = value; }

    // Each method checks FaultMap before delegating
    private void ThrowIfFaulted(string methodName)
    {
        if (FaultMap.TryGetValue(methodName, out var ex))
            throw ex;
    }

    public Task<IReadOnlyList<ProcessDescriptor>> ListProcessesAsync(CancellationToken ct = default)
    {
        ThrowIfFaulted(nameof(ListProcessesAsync));
        return _inner.ListProcessesAsync(ct);
    }

    public Task<EngineAttachment> AttachAsync(int processId, CancellationToken ct = default)
    {
        ThrowIfFaulted(nameof(AttachAsync));
        return _inner.AttachAsync(processId, ct);
    }

    public void Detach()
    {
        ThrowIfFaulted(nameof(Detach));
        _inner.Detach();
    }

    public Task<MemoryReadResult> ReadMemoryAsync(int processId, nuint address, int length, CancellationToken ct = default)
    {
        ThrowIfFaulted(nameof(ReadMemoryAsync));
        return _inner.ReadMemoryAsync(processId, address, length, ct);
    }

    public Task<TypedMemoryValue> ReadValueAsync(int processId, nuint address, MemoryDataType dataType, CancellationToken ct = default)
    {
        ThrowIfFaulted(nameof(ReadValueAsync));
        return _inner.ReadValueAsync(processId, address, dataType, ct);
    }

    public Task<MemoryWriteResult> WriteValueAsync(int processId, nuint address, MemoryDataType dataType, string value, CancellationToken ct = default)
    {
        ThrowIfFaulted(nameof(WriteValueAsync));
        return _inner.WriteValueAsync(processId, address, dataType, value, ct);
    }

    public Task<int> WriteBytesAsync(int processId, nuint address, byte[] data, CancellationToken ct = default)
    {
        ThrowIfFaulted(nameof(WriteBytesAsync));
        return _inner.WriteBytesAsync(processId, address, data, ct);
    }
}

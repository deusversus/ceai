using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// In-memory stub of IEngineFacade for unit testing without Windows P/Invoke.
/// </summary>
public sealed class StubEngineFacade : IEngineFacade
{
    private readonly Dictionary<nuint, byte[]> _memory = new();

    public IReadOnlyCollection<EngineCapability> Capabilities { get; } = new[]
    {
        EngineCapability.ProcessEnumeration, EngineCapability.MemoryRead, EngineCapability.MemoryWrite
    };

    public void WriteMemoryDirect(nuint address, byte[] data) => _memory[address] = data;

    public Task<IReadOnlyList<ProcessDescriptor>> ListProcessesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProcessDescriptor>>(new ProcessDescriptor[]
        {
            new(1000, "TestGame.exe", "x64"),
            new(2000, "notepad.exe", "x64"),
            new(3000, "calc.exe", "x86")
        });

    public Task<EngineAttachment> AttachAsync(int processId, CancellationToken ct = default) =>
        Task.FromResult(new EngineAttachment(
            processId,
            "TestGame.exe",
            new[] { new ModuleDescriptor("main.exe", (nuint)0x400000, 4096) }));

    public Task<MemoryReadResult> ReadMemoryAsync(int processId, nuint address, int length, CancellationToken ct = default)
    {
        var data = new byte[length];
        if (_memory.TryGetValue(address, out var stored))
            Array.Copy(stored, data, Math.Min(stored.Length, length));
        return Task.FromResult(new MemoryReadResult(processId, address, data));
    }

    public Task<TypedMemoryValue> ReadValueAsync(int processId, nuint address, MemoryDataType dataType, CancellationToken ct = default)
    {
        var size = dataType switch
        {
            MemoryDataType.Byte => 1,
            MemoryDataType.Int16 => 2,
            MemoryDataType.Int32 => 4,
            MemoryDataType.Int64 => 8,
            MemoryDataType.Float => 4,
            MemoryDataType.Double => 8,
            MemoryDataType.Pointer => 8,
            _ => 4
        };
        byte[] raw = new byte[size];
        if (_memory.TryGetValue(address, out var data))
            Array.Copy(data, raw, Math.Min(data.Length, raw.Length));

        string display = dataType switch
        {
            MemoryDataType.Byte => raw[0].ToString(),
            MemoryDataType.Int16 => BitConverter.ToInt16(raw).ToString(),
            MemoryDataType.Int32 => BitConverter.ToInt32(raw).ToString(),
            MemoryDataType.Float => BitConverter.ToSingle(raw).ToString(),
            _ => "0"
        };
        return Task.FromResult(new TypedMemoryValue(processId, address, dataType, display, raw));
    }

    public Task<MemoryWriteResult> WriteValueAsync(int processId, nuint address, MemoryDataType dataType, string value, CancellationToken ct = default)
    {
        byte[] bytes = dataType switch
        {
            MemoryDataType.Int32 => BitConverter.GetBytes(int.Parse(value)),
            MemoryDataType.Float => BitConverter.GetBytes(float.Parse(value)),
            _ => BitConverter.GetBytes(int.Parse(value))
        };
        _memory[address] = bytes;
        return Task.FromResult(new MemoryWriteResult(processId, address, dataType, value, bytes.Length));
    }
}

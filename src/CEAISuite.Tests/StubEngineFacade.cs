using System.Globalization;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// In-memory stub of IEngineFacade for unit testing without Windows P/Invoke.
/// </summary>
public sealed class StubEngineFacade : IEngineFacade
{
    private readonly Dictionary<nuint, byte[]> _memory = new();
    private int? _attachedProcessId;

    public int? AttachedProcessId => _attachedProcessId;

    public bool IsAttached => _attachedProcessId.HasValue;

    public IReadOnlyCollection<EngineCapability> Capabilities { get; } = new[]
    {
        EngineCapability.ProcessEnumeration, EngineCapability.MemoryRead, EngineCapability.MemoryWrite
    };

    public void WriteMemoryDirect(nuint address, byte[] data) => _memory[address] = data;

    /// <summary>Modules returned by AttachAsync. Tests can override to match their address layout.</summary>
    public ModuleDescriptor[] AttachModules { get; set; } =
        new[] { new ModuleDescriptor("main.exe", (nuint)0x400000, 4096) };

    public Task<IReadOnlyList<ProcessDescriptor>> ListProcessesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProcessDescriptor>>(new ProcessDescriptor[]
        {
            new(1000, "TestGame.exe", "x64"),
            new(2000, "notepad.exe", "x64"),
            new(3000, "calc.exe", "x86")
        });

    public Task<EngineAttachment> AttachAsync(int processId, CancellationToken ct = default)
    {
        _attachedProcessId = processId;
        return Task.FromResult(new EngineAttachment(processId, "TestGame.exe", AttachModules));
    }

    public void Detach()
    {
        _attachedProcessId = null;
    }

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
            MemoryDataType.String => 256,
            MemoryDataType.ByteArray => 64,
            _ => 4
        };
        byte[] raw = new byte[size];
        if (_memory.TryGetValue(address, out var data))
            Array.Copy(data, raw, Math.Min(data.Length, raw.Length));

        string display = dataType switch
        {
            MemoryDataType.Byte => raw[0].ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Int16 => BitConverter.ToInt16(raw).ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Int32 => BitConverter.ToInt32(raw).ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Int64 => BitConverter.ToInt64(raw).ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Float => BitConverter.ToSingle(raw).ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Double => BitConverter.ToDouble(raw).ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Pointer => $"0x{BitConverter.ToUInt64(raw):X}",
            _ => "0"
        };
        return Task.FromResult(new TypedMemoryValue(processId, address, dataType, display, raw));
    }

    public Task<MemoryWriteResult> WriteValueAsync(int processId, nuint address, MemoryDataType dataType, string value, CancellationToken ct = default)
    {
        byte[] bytes = dataType switch
        {
            MemoryDataType.Byte => new[] { byte.Parse(value, CultureInfo.InvariantCulture) },
            MemoryDataType.Int16 => BitConverter.GetBytes(short.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Int32 => BitConverter.GetBytes(int.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Int64 => BitConverter.GetBytes(long.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Float => BitConverter.GetBytes(float.Parse(value, CultureInfo.InvariantCulture)),
            MemoryDataType.Double => BitConverter.GetBytes(double.Parse(value, CultureInfo.InvariantCulture)),
            _ => BitConverter.GetBytes(int.Parse(value, CultureInfo.InvariantCulture))
        };
        _memory[address] = bytes;
        return Task.FromResult(new MemoryWriteResult(processId, address, dataType, value, bytes.Length));
    }

    public Task<int> WriteBytesAsync(int processId, nuint address, byte[] data, CancellationToken ct = default)
    {
        _memory[address] = data;
        return Task.FromResult(data.Length);
    }
}

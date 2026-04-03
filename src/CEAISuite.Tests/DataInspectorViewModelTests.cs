using CEAISuite.Desktop.ViewModels;

namespace CEAISuite.Tests;

public sealed class DataInspectorViewModelTests
{
    [Fact]
    public void Update_OneByte_ShowsInt8UInt8Binary()
    {
        var vm = new DataInspectorViewModel();
        vm.Update([0xFF], 0);

        Assert.Contains(vm.Entries, e => e.TypeName == "Int8" && e.Value == "-1");
        Assert.Contains(vm.Entries, e => e.TypeName == "UInt8" && e.Value == "255");
        Assert.Contains(vm.Entries, e => e.TypeName == "Binary" && e.Value == "11111111");
    }

    [Fact]
    public void Update_FourBytes_ShowsInt32Float()
    {
        var vm = new DataInspectorViewModel();
        // 42 28 00 00 = 10280 as Int32, and float 42.0 is 0x42280000 in big-endian, but we're little-endian
        var bytes = BitConverter.GetBytes(12345);
        vm.Update(bytes, 0);

        Assert.Contains(vm.Entries, e => e.TypeName == "Int32" && e.Value == "12345");
        Assert.Contains(vm.Entries, e => e.TypeName == "UInt32" && e.Value == "12345");
        Assert.Contains(vm.Entries, e => e.TypeName == "Float");
    }

    [Fact]
    public void Update_EightBytes_ShowsInt64DoublePointer()
    {
        var vm = new DataInspectorViewModel();
        var bytes = BitConverter.GetBytes(0x00007FFABC123456UL);
        vm.Update(bytes, 0);

        Assert.Contains(vm.Entries, e => e.TypeName == "Int64");
        Assert.Contains(vm.Entries, e => e.TypeName == "UInt64");
        Assert.Contains(vm.Entries, e => e.TypeName == "Double");
        Assert.Contains(vm.Entries, e => e.TypeName == "Pointer");
    }

    [Fact]
    public void Update_BigEndian_ReversesInterpretation()
    {
        var vm = new DataInspectorViewModel();

        // Little-endian: 01 00 = Int16 value 1
        // Big-endian:    01 00 = Int16 value 256
        vm.Update([0x01, 0x00], 0);
        var leEntry = vm.Entries.First(e => e.TypeName == "Int16");
        Assert.Equal("1", leEntry.Value);

        vm.IsBigEndian = true;
        // After Bug 18 fix, changing IsBigEndian should auto-refresh
        var beEntry = vm.Entries.First(e => e.TypeName == "Int16");
        Assert.Equal("256", beEntry.Value);
    }

    [Fact]
    public void Update_EmptyBuffer_ClearsEntries()
    {
        var vm = new DataInspectorViewModel();
        vm.Update([0xFF], 0);
        Assert.NotEmpty(vm.Entries);

        vm.Update([], 0);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void Update_OffsetOutOfRange_ClearsEntries()
    {
        var vm = new DataInspectorViewModel();
        vm.Update([0x01, 0x02], 5);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void Update_AsciiString_ShowsASCII()
    {
        var vm = new DataInspectorViewModel();
        var bytes = "Hello\0World"u8.ToArray();
        vm.Update(bytes, 0);

        Assert.Contains(vm.Entries, e => e.TypeName == "ASCII" && e.Value == "Hello");
    }
}

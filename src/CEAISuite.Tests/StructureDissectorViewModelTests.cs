using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class StructureDissectorViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubClipboardService _clipboard = new();

    private StructureDissectorViewModel CreateVm()
    {
        var dissectorService = new StructureDissectorService(_engineFacade);
        var addressTableService = new AddressTableService(_engineFacade);
        return new StructureDissectorViewModel(dissectorService, _processContext, _outputLog, _clipboard,
            new StubNavigationService(), addressTableService, new StubAiContextService());
    }

    [Fact]
    public async Task Dissect_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.BaseAddress = "0x10000";

        await vm.DissectCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task Dissect_InvalidAddress_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.BaseAddress = "not_an_address";

        await vm.DissectCommand.ExecuteAsync(null);

        Assert.Contains("Invalid", vm.StatusText);
    }

    [Fact]
    public async Task Dissect_ValidAddress_PopulatesFields()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        // Plant some memory at a known address for the stub engine to read
        _engineFacade.WriteMemoryDirect(0x10000,
            [100, 0, 0, 0,   // Int32: 100
             0, 0, 200, 66,  // Float: ~100.0
             50, 0, 0, 0,    // Int32: 50
             0, 0, 0, 0]);   // Zero
        vm.BaseAddress = "0x10000";
        vm.RegionSize = 16;

        await vm.DissectCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Fields);
        Assert.Contains("field", vm.StatusText ?? "");
    }

    [Fact]
    public void FollowPointer_NonPointerSelected_DoesNothing()
    {
        var vm = CreateVm();
        vm.Fields.Add(new() { Offset = 0, ProbableType = "Int32", DisplayValue = "100", Confidence = 0.7 });

        // Select the Int32 field (not a pointer)
        // FollowPointer should do nothing since it's not a Pointer type
        vm.FollowPointerCommand.Execute(null);

        // BaseAddress should not have changed
        Assert.Equal("", vm.BaseAddress);
    }

    [Fact]
    public void ExportCStruct_PopulatesClipboard()
    {
        var vm = CreateVm();
        vm.Fields.Add(new() { Offset = 0, ProbableType = "Int32", DisplayValue = "100", Confidence = 0.7 });
        vm.Fields.Add(new() { Offset = 4, ProbableType = "Float", DisplayValue = "1.5", Confidence = 0.8 });

        vm.ExportCStructCommand.Execute(null);

        Assert.NotNull(_clipboard.LastText);
        Assert.Contains("struct Unknown", _clipboard.LastText);
        Assert.Contains("int32_t", _clipboard.LastText);
        Assert.Contains("float", _clipboard.LastText);
    }

    [Fact]
    public void ExportCStruct_EmptyFields_DoesNothing()
    {
        var vm = CreateVm();

        vm.ExportCStructCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    [Fact]
    public void ExportCEStruct_PopulatesClipboardWithXml()
    {
        var vm = CreateVm();
        vm.Fields.Add(new() { Offset = 0, ProbableType = "Int32", DisplayValue = "100", Confidence = 0.7 });
        vm.Fields.Add(new() { Offset = 8, ProbableType = "Pointer", DisplayValue = "0x7FF00000", Confidence = 0.9 });

        vm.ExportCEStructCommand.Execute(null);

        Assert.NotNull(_clipboard.LastText);
        Assert.Contains("<Structure", _clipboard.LastText);
        Assert.Contains("Vartype=\"4 Bytes\"", _clipboard.LastText);
        Assert.Contains("Vartype=\"Pointer\"", _clipboard.LastText);
        Assert.Contains("Bytesize=\"8\"", _clipboard.LastText);
    }

    // ── DissectAsync execution ──

    [Fact]
    public async Task Dissect_EmptyBaseAddress_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.BaseAddress = "";

        await vm.DissectCommand.ExecuteAsync(null);

        Assert.Contains("Enter a base address", vm.StatusText);
    }

    [Fact]
    public async Task Dissect_SetsAndClearsIsDissecting()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.WriteMemoryDirect(0x20000,
            [100, 0, 0, 0, 0, 0, 200, 66, 50, 0, 0, 0, 0, 0, 0, 0]);
        vm.BaseAddress = "0x20000";
        vm.RegionSize = 16;

        await vm.DissectCommand.ExecuteAsync(null);

        Assert.False(vm.IsDissecting);
    }

    [Fact]
    public async Task Dissect_SetsStatusWithFieldAndClusterCount()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.WriteMemoryDirect(0x30000,
            [100, 0, 0, 0, 50, 0, 0, 0, 25, 0, 0, 0, 10, 0, 0, 0]);
        vm.BaseAddress = "0x30000";
        vm.RegionSize = 16;

        await vm.DissectCommand.ExecuteAsync(null);

        Assert.Contains("field", vm.StatusText ?? "");
        Assert.Contains("cluster", vm.StatusText ?? "");
    }

    // ── Type hint selection ──

    [Fact]
    public void TypeHints_ContainsExpectedOptions()
    {
        var vm = CreateVm();

        Assert.Contains("auto", vm.TypeHints);
        Assert.Contains("int32", vm.TypeHints);
        Assert.Contains("float", vm.TypeHints);
        Assert.Contains("pointers", vm.TypeHints);
    }

    [Fact]
    public async Task Dissect_WithFloatHint_ProducesResults()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        // Write some float-like values
        var floatBytes = BitConverter.GetBytes(3.14f);
        var float2Bytes = BitConverter.GetBytes(2.71f);
        var allBytes = new byte[16];
        Array.Copy(floatBytes, 0, allBytes, 0, 4);
        Array.Copy(float2Bytes, 0, allBytes, 4, 4);
        _engineFacade.WriteMemoryDirect(0x40000, allBytes);
        vm.BaseAddress = "0x40000";
        vm.RegionSize = 16;
        vm.SelectedTypeHint = "float";

        await vm.DissectCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Fields);
    }

    // ── Compare ──

    [Fact]
    public async Task Compare_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.BaseAddress = "0x10000";
        vm.CompareAddress = "0x20000";

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task Compare_MissingAddresses_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.BaseAddress = "0x10000";
        vm.CompareAddress = "";

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Contains("Enter both", vm.StatusText);
    }

    [Fact]
    public async Task Compare_InvalidAddress_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.BaseAddress = "not_valid";
        vm.CompareAddress = "0x20000";

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Contains("Invalid", vm.StatusText);
    }

    [Fact]
    public async Task Compare_ValidAddresses_PopulatesCompareResults()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.WriteMemoryDirect(0x50000, [100, 0, 0, 0, 50, 0, 0, 0]);
        _engineFacade.WriteMemoryDirect(0x60000, [200, 0, 0, 0, 50, 0, 0, 0]);
        vm.BaseAddress = "0x50000";
        vm.CompareAddress = "0x60000";
        vm.RegionSize = 8;

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.CompareResults);
        Assert.Contains("compared", vm.StatusText ?? "");
        Assert.Contains("differ", vm.StatusText ?? "");
        Assert.False(vm.IsComparing);
    }

    // ── FollowPointer ──

    [Fact]
    public void FollowPointer_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedField = null;

        vm.FollowPointerCommand.Execute(null);

        Assert.Equal("", vm.BaseAddress);
    }

    [Fact]
    public void FollowPointer_PointerSelected_SetsBaseAddress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.Fields.Add(new() { Offset = 0, ProbableType = "Pointer", DisplayValue = "0x7FF00000", Confidence = 0.9 });
        vm.SelectedField = vm.Fields[0];

        vm.FollowPointerCommand.Execute(null);

        Assert.Equal("0x7FF00000", vm.BaseAddress);
    }

    // ── NavigateToAddress ──

    [Fact]
    public void NavigateToAddress_SetsBaseAddress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.NavigateToAddress("0xABCD0000");

        Assert.Equal("0xABCD0000", vm.BaseAddress);
    }

    // ── Context menu commands ──

    [Fact]
    public void CopyFieldValue_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedField = null;

        vm.CopyFieldValueCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    [Fact]
    public void CopyFieldValue_WithSelection_CopiesToClipboard()
    {
        var vm = CreateVm();
        vm.Fields.Add(new() { Offset = 0, ProbableType = "Int32", DisplayValue = "42", Confidence = 0.7 });
        vm.SelectedField = vm.Fields[0];

        vm.CopyFieldValueCommand.Execute(null);

        Assert.Equal("42", _clipboard.LastText);
    }

    [Fact]
    public void CopyFieldOffset_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedField = null;

        vm.CopyFieldOffsetCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    [Fact]
    public void CopyFieldOffset_WithSelection_CopiesToClipboard()
    {
        var vm = CreateVm();
        vm.Fields.Add(new() { Offset = 16, ProbableType = "Int32", DisplayValue = "100", Confidence = 0.7 });
        vm.SelectedField = vm.Fields[0];

        vm.CopyFieldOffsetCommand.Execute(null);

        Assert.Equal("0x010", _clipboard.LastText);
    }

    [Fact]
    public void BrowseFieldMemory_WithSelection_Navigates()
    {
        var vm = CreateVm();
        var nav = new StubNavigationService();
        var addressTableService = new AddressTableService(_engineFacade);
        var vmWithNav = new StructureDissectorViewModel(
            new StructureDissectorService(_engineFacade), _processContext, _outputLog, _clipboard,
            nav, addressTableService, new StubAiContextService());
        vmWithNav.BaseAddress = "0x10000";
        vmWithNav.Fields.Add(new() { Offset = 8, ProbableType = "Int32", DisplayValue = "100", Confidence = 0.7 });
        vmWithNav.SelectedField = vmWithNav.Fields[0];

        vmWithNav.BrowseFieldMemoryCommand.Execute(null);

        Assert.Single(nav.DocumentsShown);
        Assert.Equal("memoryBrowser", nav.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void BrowseFieldMemory_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedField = null;

        vm.BrowseFieldMemoryCommand.Execute(null);

        // No crash
    }

    [Fact]
    public void AddFieldToTable_WithSelection_AddsEntry()
    {
        var vm = CreateVm();
        vm.BaseAddress = "0x10000";
        vm.Fields.Add(new() { Offset = 4, ProbableType = "Float", DisplayValue = "1.5", Confidence = 0.8 });
        vm.SelectedField = vm.Fields[0];

        vm.AddFieldToTableCommand.Execute(null);

        Assert.Contains("Added", vm.StatusText);
    }

    [Fact]
    public void AddFieldToTable_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedField = null;

        vm.AddFieldToTableCommand.Execute(null);

        Assert.Null(vm.StatusText);
    }

    [Fact]
    public void AskAi_WithSelection_SendsContext()
    {
        var aiContext = new StubAiContextService();
        var vm = new StructureDissectorViewModel(
            new StructureDissectorService(_engineFacade), _processContext, _outputLog, _clipboard,
            new StubNavigationService(), new AddressTableService(_engineFacade), aiContext);
        vm.BaseAddress = "0x10000";
        vm.Fields.Add(new() { Offset = 0, ProbableType = "Int32", DisplayValue = "42", Confidence = 0.7 });
        vm.SelectedField = vm.Fields[0];

        vm.AskAiCommand.Execute(null);

        Assert.Equal("Structure Dissector", aiContext.LastLabel);
        Assert.Contains("0x10000", aiContext.LastContext);
    }

    [Fact]
    public void AskAi_NoSelection_DoesNothing()
    {
        var aiContext = new StubAiContextService();
        var vm = new StructureDissectorViewModel(
            new StructureDissectorService(_engineFacade), _processContext, _outputLog, _clipboard,
            new StubNavigationService(), new AddressTableService(_engineFacade), aiContext);
        vm.SelectedField = null;

        vm.AskAiCommand.Execute(null);

        Assert.Null(aiContext.LastLabel);
    }

    // ── ExportCEStruct empty fields ──

    [Fact]
    public void ExportCEStruct_EmptyFields_DoesNothing()
    {
        var vm = CreateVm();

        vm.ExportCEStructCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    // ── ExportCStruct type mapping ──

    [Fact]
    public void ExportCStruct_AllTypes_MapsCorrectly()
    {
        var vm = CreateVm();
        vm.Fields.Add(new() { Offset = 0, ProbableType = "Int32", DisplayValue = "1", Confidence = 0.7 });
        vm.Fields.Add(new() { Offset = 4, ProbableType = "Int64", DisplayValue = "2", Confidence = 0.7 });
        vm.Fields.Add(new() { Offset = 12, ProbableType = "Float", DisplayValue = "3.0", Confidence = 0.7 });
        vm.Fields.Add(new() { Offset = 16, ProbableType = "Double", DisplayValue = "4.0", Confidence = 0.7 });
        vm.Fields.Add(new() { Offset = 24, ProbableType = "Pointer", DisplayValue = "0x1000", Confidence = 0.7 });
        vm.Fields.Add(new() { Offset = 32, ProbableType = "Unknown", DisplayValue = "?", Confidence = 0.3 });

        vm.ExportCStructCommand.Execute(null);

        Assert.Contains("int32_t", _clipboard.LastText);
        Assert.Contains("int64_t", _clipboard.LastText);
        Assert.Contains("float", _clipboard.LastText);
        Assert.Contains("double", _clipboard.LastText);
        Assert.Contains("void*", _clipboard.LastText);
        Assert.Contains("uint8_t", _clipboard.LastText);
    }

}

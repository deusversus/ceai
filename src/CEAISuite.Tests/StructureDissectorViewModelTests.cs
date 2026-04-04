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
}

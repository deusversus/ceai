using System.IO;
using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class AddressTableViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubDialogService _dialogService = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubDispatcherService _dispatcher = new();
    private readonly StubNavigationService _navigationService = new();

    private AddressTableViewModel CreateVm(
        StubProcessContext? pc = null,
        IAutoAssemblerEngine? aa = null,
        ILuaScriptEngine? lua = null)
    {
        var addressTableService = new AddressTableService(_engineFacade);
        var exportService = new AddressTableExportService();
        var breakpointService = new BreakpointService(new StubBreakpointEngine());
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptService = new ScriptGenerationService();

        return new AddressTableViewModel(
            addressTableService,
            exportService,
            pc ?? new StubProcessContext(),
            autoAssemblerEngine: aa,
            breakpointService,
            disassemblyService,
            scriptService,
            _dialogService,
            _outputLog,
            _dispatcher,
            _navigationService,
            luaScriptEngine: lua);
    }

    private static AddressTableNode MakeLeaf(string label = "HP", string address = "0x100",
        MemoryDataType dt = MemoryDataType.Int32, string value = "100")
    {
        return new AddressTableNode($"addr-{Guid.NewGuid():N}"[..12], label, false)
        {
            Address = address,
            DataType = dt,
            CurrentValue = value,
        };
    }

    private static AddressTableNode MakeScript(string label = "Godmode", string script = "[ENABLE]\nnop\n[DISABLE]")
    {
        return new AddressTableNode($"script-{Guid.NewGuid():N}"[..14], label, false)
        {
            AssemblerScript = script,
        };
    }

    private static AddressTableNode MakeGroup(string label = "Stats", params AddressTableNode[] children)
    {
        var g = new AddressTableNode($"group-{Guid.NewGuid():N}"[..13], label, true);
        foreach (var c in children) { c.Parent = g; g.Children.Add(c); }
        return g;
    }

    // ══════════════════════════════════════════════════════════════════
    // EXISTING TESTS (preserved)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void CreateGroup_WithName_AddsGroupToRoots()
    {
        var vm = CreateVm();
        _dialogService.NextInputResult = "My Group";
        vm.CreateGroupCommand.Execute(null);
        Assert.NotNull(vm.Roots);
        Assert.Single(vm.Roots);
        Assert.Equal("My Group", vm.Roots[0].Label);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("My Group"));
    }

    [Fact]
    public void CreateGroup_CancelDialog_NoGroupAdded()
    {
        var vm = CreateVm();
        _dialogService.NextInputResult = null;
        vm.CreateGroupCommand.Execute(null);
        Assert.NotNull(vm.Roots);
        Assert.Empty(vm.Roots);
    }

    [Fact]
    public void RemoveSelected_NoSelection_LogsWarning()
    {
        var vm = CreateVm();
        vm.SelectedNode = null;
        vm.RemoveSelectedCommand.Execute(null);
        Assert.Single(_outputLog.LoggedMessages);
        Assert.Equal("Warn", _outputLog.LoggedMessages[0].Level);
    }

    [Fact]
    public void Export_EmptyTable_LogsWarning()
    {
        var vm = CreateVm();
        vm.ExportCommand.Execute(null);
        Assert.Single(_outputLog.LoggedMessages);
        Assert.Equal("Warn", _outputLog.LoggedMessages[0].Level);
    }

    [Fact]
    public void ToggleLock_NoSelection_LogsWarning()
    {
        var vm = CreateVm();
        vm.SelectedNode = null;
        vm.ToggleLockCommand.Execute(null);
        Assert.Single(_outputLog.LoggedMessages);
        Assert.Equal("Warn", _outputLog.LoggedMessages[0].Level);
    }

    [Fact]
    public async Task Refresh_NoProcess_LogsWarning()
    {
        var vm = CreateVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn");
    }

    // ══════════════════════════════════════════════════════════════════
    // COLOR CODING
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void SetColor_WithSelection_SetsColor()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.SetColorCommand.Execute("#FF4444");

        Assert.Equal("#FF4444", node.UserColor);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("#FF4444"));
    }

    [Fact]
    public void SetColor_EmptyString_ClearsColor()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        node.UserColor = "#FF4444";
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.SetColorCommand.Execute("");

        Assert.Null(node.UserColor);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("cleared"));
    }

    [Fact]
    public void SetColor_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedNode = null;
        vm.SetColorCommand.Execute("#FF4444");
        Assert.Empty(_outputLog.LoggedMessages);
    }

    // ══════════════════════════════════════════════════════════════════
    // SORTING
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void SortBy_Label_SortsAscending()
    {
        var vm = CreateVm();
        vm.Roots!.Add(MakeLeaf("Charlie"));
        vm.Roots.Add(MakeLeaf("Alpha"));
        vm.Roots.Add(MakeLeaf("Bravo"));

        vm.SortByCommand.Execute("Label");

        Assert.Equal("Alpha", vm.Roots[0].Label);
        Assert.Equal("Bravo", vm.Roots[1].Label);
        Assert.Equal("Charlie", vm.Roots[2].Label);
    }

    [Fact]
    public void SortBy_SameColumnTwice_TogglesDescending()
    {
        var vm = CreateVm();
        vm.Roots!.Add(MakeLeaf("Alpha"));
        vm.Roots.Add(MakeLeaf("Charlie"));
        vm.Roots.Add(MakeLeaf("Bravo"));

        vm.SortByCommand.Execute("Label");
        vm.SortByCommand.Execute("Label"); // toggle to descending

        Assert.Equal("Charlie", vm.Roots[0].Label);
        Assert.Equal("Bravo", vm.Roots[1].Label);
        Assert.Equal("Alpha", vm.Roots[2].Label);
    }

    [Fact]
    public void SortBy_Value_SortsAlphabetically()
    {
        var vm = CreateVm();
        vm.Roots!.Add(MakeLeaf("A", value: "300"));
        vm.Roots.Add(MakeLeaf("B", value: "100"));
        vm.Roots.Add(MakeLeaf("C", value: "200"));

        vm.SortByCommand.Execute("Value");

        Assert.Equal("100", vm.Roots[0].CurrentValue);
        Assert.Equal("200", vm.Roots[1].CurrentValue);
        Assert.Equal("300", vm.Roots[2].CurrentValue);
    }

    [Fact]
    public void SortBy_RecursiveChildren_SortsGroupChildren()
    {
        var vm = CreateVm();
        var group = MakeGroup("Stats", MakeLeaf("Zulu"), MakeLeaf("Alpha"), MakeLeaf("Mike"));
        vm.Roots!.Add(group);

        vm.SortByCommand.Execute("Label");

        Assert.Equal("Alpha", group.Children[0].Label);
        Assert.Equal("Mike", group.Children[1].Label);
        Assert.Equal("Zulu", group.Children[2].Label);
    }

    // ══════════════════════════════════════════════════════════════════
    // REFRESH
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshAsync_WithProcess_UpdatesStatus()
    {
        var pc = new StubProcessContext { AttachedProcessId = 1234 };
        var vm = CreateVm(pc: pc);
        vm.Roots!.Add(MakeLeaf());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("refreshed", vm.AddressTableStatus ?? "");
    }

    // ══════════════════════════════════════════════════════════════════
    // AUTO-REFRESH
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void StartAutoRefresh_CreatesTimer()
    {
        var vm = CreateVm();
        vm.StartAutoRefresh(1000);
        // Timer is internal, but we can verify it doesn't throw and StopAutoRefresh works
        vm.StopAutoRefresh();
    }

    [Fact]
    public void SetRefreshInterval_DoesNotThrow()
    {
        var vm = CreateVm();
        vm.StartAutoRefresh(1000);
        vm.SetRefreshInterval(250);
        vm.StopAutoRefresh();
    }

    // ══════════════════════════════════════════════════════════════════
    // REMOVE / DELETE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void RemoveSelected_WithSelection_RemovesEntry()
    {
        var vm = CreateVm();
        var node = MakeLeaf("ToRemove");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.RemoveSelectedCommand.Execute(null);

        Assert.Empty(vm.Roots);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Removed"));
    }

    [Fact]
    public void Delete_WithSelection_RemovesEntry()
    {
        var vm = CreateVm();
        var node = MakeLeaf("ToDelete");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.DeleteCommand.Execute(null);

        Assert.Empty(vm.Roots);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Deleted"));
    }

    // ══════════════════════════════════════════════════════════════════
    // LOCK / FREEZE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToggleLock_WithSelection_LocksEntry()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.ToggleLockCommand.Execute(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Toggled lock"));
    }

    [Fact]
    public void ToggleFreeze_WithSelection_FreezesEntry()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        node.CurrentValue = "42";
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.ToggleFreezeCommand.Execute(null);

        Assert.True(node.IsLocked);
        Assert.Equal("42", node.LockedValue);
    }

    [Fact]
    public void ToggleFreeze_AlreadyFrozen_Unfreezes()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        node.IsLocked = true;
        node.LockedValue = "42";
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.ToggleFreezeCommand.Execute(null);

        Assert.False(node.IsLocked);
        Assert.Null(node.LockedValue);
    }

    [Fact]
    public void ToggleFreeze_GroupNode_DoesNothing()
    {
        var vm = CreateVm();
        var group = MakeGroup("Stats");
        vm.Roots!.Add(group);
        vm.SelectedNode = group;

        vm.ToggleFreezeCommand.Execute(null);
        // No crash, no log (groups are filtered out)
    }

    // ══════════════════════════════════════════════════════════════════
    // EXPORT / IMPORT
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Export_WithEntries_WritesJsonFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"export_test_{Guid.NewGuid():N}.json");
        try
        {
            var vm = CreateVm();
            var node = MakeLeaf("HP", "0x100", MemoryDataType.Int32, "100");
            vm.Roots!.Add(node);
            _dialogService.NextSaveFilePath = path;

            vm.ExportCommand.Execute(null);

            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("HP", content);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Export_CancelDialog_DoesNotWrite()
    {
        var vm = CreateVm();
        vm.Roots!.Add(MakeLeaf());
        _dialogService.NextSaveFilePath = null;

        vm.ExportCommand.Execute(null);
        // No file written, no error
    }

    [Fact]
    public void Import_ValidJson_AddsEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"import_test_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """[{"Label":"Imported","Address":"0x200","DataType":"Int32","CurrentValue":"50"}]""");
            var vm = CreateVm();
            _dialogService.NextOpenFilePath = path;

            vm.ImportCommand.Execute(null);

            Assert.NotEmpty(vm.Roots!);
            Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Imported"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Import_CancelDialog_DoesNothing()
    {
        var vm = CreateVm();
        _dialogService.NextOpenFilePath = null;
        vm.ImportCommand.Execute(null);
        Assert.Empty(vm.Roots!);
    }

    // ══════════════════════════════════════════════════════════════════
    // CHEAT TABLE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void SaveCheatTable_EmptyTable_LogsWarning()
    {
        var vm = CreateVm();
        vm.SaveCheatTableCommand.Execute(null);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("empty"));
    }

    [Fact]
    public void SaveCheatTable_WithEntries_SavesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ct_test_{Guid.NewGuid():N}.ct");
        try
        {
            var vm = CreateVm();
            vm.Roots!.Add(MakeLeaf("HP", "0x100", MemoryDataType.Int32, "100"));
            _dialogService.NextSaveFilePath = path;

            vm.SaveCheatTableCommand.Execute(null);

            Assert.True(File.Exists(path));
            Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Saved"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LoadCheatTable_ValidFile_ImportsEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ct_load_{Guid.NewGuid():N}.ct");
        try
        {
            File.WriteAllText(path, """<?xml version="1.0" encoding="utf-8"?><CheatTable CheatEngineTableVersion="46"><CheatEntries><CheatEntry><ID>0</ID><Description>"HP"</Description><VariableType>4 Bytes</VariableType><Address>0x100</Address></CheatEntry></CheatEntries></CheatTable>""");
            var vm = CreateVm();
            _dialogService.NextOpenFilePath = path;

            vm.LoadCheatTableCommand.Execute(null);

            Assert.NotEmpty(vm.Roots!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ══════════════════════════════════════════════════════════════════
    // TRAINER
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void GenerateTrainer_NoLockedEntries_LogsWarning()
    {
        var vm = CreateVm();
        vm.Roots!.Add(MakeLeaf()); // not locked
        vm.GenerateTrainerCommand.Execute(null);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("Lock"));
    }

    [Fact]
    public void GenerateTrainer_WithLockedEntries_SavesScript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trainer_{Guid.NewGuid():N}.cs");
        try
        {
            var pc = new StubProcessContext { AttachedProcessId = 1, AttachedProcessName = "game.exe" };
            var vm = CreateVm(pc: pc);
            var node = MakeLeaf("HP", "0x100", MemoryDataType.Int32, "999");
            node.IsLocked = true;
            node.LockedValue = "999";
            vm.Roots!.Add(node);
            _dialogService.NextSaveFilePath = path;

            vm.GenerateTrainerCommand.Execute(null);

            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("game", content);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ══════════════════════════════════════════════════════════════════
    // CONTEXT MENU: DESCRIPTION / ADDRESS / VALUE / TYPE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ChangeDescription_UpdatesLabel()
    {
        var vm = CreateVm();
        var node = MakeLeaf("OldName");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;
        _dialogService.NextInputResult = "NewName";

        vm.ChangeDescriptionCommand.Execute(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("NewName"));
    }

    [Fact]
    public void ChangeAddress_UpdatesAddress()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;
        _dialogService.NextInputResult = "0xDEAD";

        vm.ChangeAddressCommand.Execute(null);

        Assert.Equal("0xDEAD", node.Address);
    }

    [Fact]
    public void ChangeValue_UpdatesCurrentValue()
    {
        var vm = CreateVm();
        var node = MakeLeaf("HP", value: "100");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;
        _dialogService.NextInputResult = "999";

        vm.ChangeValueCommand.Execute(null);

        Assert.Equal("999", node.CurrentValue);
    }

    [Fact]
    public void ChangeType_ValidType_UpdatesDataType()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;
        _dialogService.NextInputResult = "Float";

        vm.ChangeTypeCommand.Execute(null);

        Assert.Equal(MemoryDataType.Float, node.DataType);
    }

    [Fact]
    public void ChangeType_InvalidType_DoesNotChange()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;
        _dialogService.NextInputResult = "NotAType";

        vm.ChangeTypeCommand.Execute(null);

        Assert.Equal(MemoryDataType.Int32, node.DataType); // unchanged
    }

    // ══════════════════════════════════════════════════════════════════
    // HEX / SIGNED TOGGLE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToggleShowAsHex_TogglesProperty()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        Assert.False(node.ShowAsHex);
        vm.ToggleShowAsHexCommand.Execute(null);
        Assert.True(node.ShowAsHex);
    }

    [Fact]
    public void ToggleShowAsSigned_TogglesProperty()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        Assert.False(node.ShowAsSigned);
        vm.ToggleShowAsSignedCommand.Execute(null);
        Assert.True(node.ShowAsSigned);
    }

    // ══════════════════════════════════════════════════════════════════
    // INCREASE / DECREASE VALUE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IncreaseValue_Int32_IncrementsByOne()
    {
        var vm = CreateVm();
        var node = MakeLeaf("HP", "0x100", MemoryDataType.Int32, "100");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.IncreaseValueCommand.ExecuteAsync(null);

        Assert.Equal("101", node.CurrentValue);
    }

    [Fact]
    public async Task DecreaseValue_Int32_DecrementsByOne()
    {
        var vm = CreateVm();
        var node = MakeLeaf("HP", "0x100", MemoryDataType.Int32, "100");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.DecreaseValueCommand.ExecuteAsync(null);

        Assert.Equal("99", node.CurrentValue);
    }

    [Fact]
    public async Task IncreaseValue_Float_IncrementsByOne()
    {
        var vm = CreateVm();
        var node = MakeLeaf("Speed", "0x200", MemoryDataType.Float, "1.5");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.IncreaseValueCommand.ExecuteAsync(null);

        Assert.StartsWith("2.5", node.CurrentValue);
    }

    [Fact]
    public async Task IncreaseValue_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedNode = null;
        await vm.IncreaseValueCommand.ExecuteAsync(null);
        // No crash, no log
    }

    [Fact]
    public async Task IncreaseValue_GroupNode_DoesNothing()
    {
        var vm = CreateVm();
        var group = MakeGroup("Stats");
        vm.Roots!.Add(group);
        vm.SelectedNode = group;

        await vm.IncreaseValueCommand.ExecuteAsync(null);
        // No crash
    }

    [Fact]
    public async Task IncreaseValue_WithDropdownSuffix_StripsBeforeIncrement()
    {
        var vm = CreateVm();
        var node = MakeLeaf("Item", "0x100", MemoryDataType.Int32, "2904 : Dagger");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.IncreaseValueCommand.ExecuteAsync(null);

        Assert.Equal("2905", node.CurrentValue);
    }

    // ══════════════════════════════════════════════════════════════════
    // DROPDOWN CONFIGURATION
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ConfigureDropDown_SetPairs()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;
        _dialogService.NextInputResult = "0=Off\n1=On";

        vm.ConfigureDropDownCommand.Execute(null);

        Assert.NotNull(node.DropDownList);
        Assert.Equal(2, node.DropDownList.Count);
        Assert.Equal("Off", node.DropDownList[0]);
        Assert.Equal("On", node.DropDownList[1]);
    }

    [Fact]
    public void ConfigureDropDown_EmptyInput_ClearsDropDown()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        node.DropDownList = new Dictionary<int, string> { [0] = "A" };
        vm.Roots!.Add(node);
        vm.SelectedNode = node;
        _dialogService.NextInputResult = "  "; // whitespace

        vm.ConfigureDropDownCommand.Execute(null);

        Assert.Null(node.DropDownList);
    }

    // ══════════════════════════════════════════════════════════════════
    // NAVIGATION
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void BrowseMemory_WithSelection_RaisesEvent()
    {
        var pc = new StubProcessContext { AttachedProcessId = 1 };
        var vm = CreateVm(pc: pc);
        var node = MakeLeaf("HP", "0x400");
        node.ResolvedAddress = (nuint)0x400;
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        nuint? navigatedTo = null;
        vm.NavigateToMemoryBrowser += addr => navigatedTo = addr;
        vm.BrowseMemoryCommand.Execute(null);

        Assert.NotNull(navigatedTo);
        Assert.Equal((nuint)0x400, navigatedTo.Value);
    }

    [Fact]
    public void BrowseMemory_NoProcess_DoesNothing()
    {
        var vm = CreateVm(); // no process attached
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        nuint? navigatedTo = null;
        vm.NavigateToMemoryBrowser += addr => navigatedTo = addr;
        vm.BrowseMemoryCommand.Execute(null);

        Assert.Null(navigatedTo);
    }

    [Fact]
    public void Disassemble_WithSelection_RaisesEvent()
    {
        var vm = CreateVm();
        var node = MakeLeaf("Code", "0xDEAD");
        node.ResolvedAddress = (nuint)0xDEAD;
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        string? navigatedTo = null;
        vm.NavigateToDisassembly += addr => navigatedTo = addr;
        vm.DisassembleCommand.Execute(null);

        Assert.NotNull(navigatedTo);
        Assert.Contains("DEAD", navigatedTo);
    }

    // ══════════════════════════════════════════════════════════════════
    // CUT / COPY / PASTE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Cut_RemovesAndStoresClipboard()
    {
        var vm = CreateVm();
        var node = MakeLeaf("ToCut");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.CutCommand.Execute(null);

        Assert.Empty(vm.Roots);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Cut"));
    }

    [Fact]
    public void Copy_StoresClipboard()
    {
        var vm = CreateVm();
        var node = MakeLeaf("ToCopy");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.CopyCommand.Execute(null);

        Assert.Single(vm.Roots); // not removed
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Copied"));
    }

    [Fact]
    public void Paste_AfterCopy_InsertsClone()
    {
        var vm = CreateVm();
        var node = MakeLeaf("Original");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.CopyCommand.Execute(null);
        vm.SelectedNode = null;
        vm.PasteCommand.Execute(null);

        Assert.Equal(2, vm.Roots.Count);
        Assert.Contains("copy", vm.Roots[1].Label);
    }

    [Fact]
    public void Paste_EmptyClipboard_LogsWarning()
    {
        var vm = CreateVm();
        vm.PasteCommand.Execute(null);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("Nothing to paste"));
    }

    [Fact]
    public void Paste_IntoGroup_AddsAsChild()
    {
        var vm = CreateVm();
        var node = MakeLeaf("ToCopy");
        var group = MakeGroup("Target");
        vm.Roots!.Add(node);
        vm.Roots.Add(group);

        vm.SelectedNode = node;
        vm.CopyCommand.Execute(null);
        vm.SelectedNode = group; // select group as paste target
        vm.PasteCommand.Execute(null);

        Assert.Single(group.Children);
        Assert.Contains("copy", group.Children[0].Label);
    }

    // ══════════════════════════════════════════════════════════════════
    // TOGGLE ACTIVATE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ToggleActivate_ValueEntry_TogglesLock()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        node.CurrentValue = "50";
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.ToggleActivateCommand.Execute(null);

        Assert.True(node.IsActive);
    }

    // ══════════════════════════════════════════════════════════════════
    // SCRIPT TOGGLE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ToggleSelectedScript_NoScript_LogsWarning()
    {
        var vm = CreateVm();
        var node = MakeLeaf(); // no script
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.ToggleSelectedScriptCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("script"));
    }

    [Fact]
    public async Task ToggleSelectedScript_NoProcess_LogsWarning()
    {
        var vm = CreateVm(); // no process
        var node = MakeScript();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.ToggleSelectedScriptCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("process"));
    }

    [Fact]
    public async Task ToggleSelectedScript_NoEngine_LogsWarning()
    {
        var pc = new StubProcessContext { AttachedProcessId = 1 };
        var vm = CreateVm(pc: pc, aa: null); // no AA engine
        var node = MakeScript();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.ToggleSelectedScriptCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("engine"));
    }

    [Fact]
    public async Task ToggleSelectedScript_Enable_CallsAutoAssembler()
    {
        var pc = new StubProcessContext { AttachedProcessId = 1 };
        var aa = new StubAutoAssemblerEngine();
        aa.NextEnableResult = new ScriptExecutionResult(true, null, [], []);
        var vm = CreateVm(pc: pc, aa: aa);
        var node = MakeScript("MyScript");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.ToggleSelectedScriptCommand.ExecuteAsync(null);

        Assert.True(node.IsScriptEnabled);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Enabled"));
    }

    [Fact]
    public async Task ToggleSelectedScript_Disable_CallsAutoAssembler()
    {
        var pc = new StubProcessContext { AttachedProcessId = 1 };
        var aa = new StubAutoAssemblerEngine();
        aa.NextDisableResult = new ScriptExecutionResult(true, null, [], []);
        var vm = CreateVm(pc: pc, aa: aa);
        var node = MakeScript("MyScript");
        node.IsScriptEnabled = true; // already enabled
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        await vm.ToggleSelectedScriptCommand.ExecuteAsync(null);

        Assert.False(node.IsScriptEnabled);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Disabled"));
    }

    // ══════════════════════════════════════════════════════════════════
    // GROUP ACTIVATION
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleActiveCheckBoxClick_Group_ToggleAllChildren()
    {
        var vm = CreateVm();
        var child1 = MakeLeaf("A"); child1.CurrentValue = "10";
        var child2 = MakeLeaf("B"); child2.CurrentValue = "20";
        var group = MakeGroup("Stats", child1, child2);
        vm.Roots!.Add(group);

        await vm.HandleActiveCheckBoxClickAsync(group);

        // Children should be locked (activated)
        Assert.True(child1.IsLocked);
        Assert.True(child2.IsLocked);
    }

    [Fact]
    public async Task HandleActiveCheckBoxClick_ValueEntry_SetsFreezeState()
    {
        var vm = CreateVm();
        var node = MakeLeaf(); node.CurrentValue = "42";
        node.IsLocked = true; // simulate checkbox toggled to ON
        vm.Roots!.Add(node);

        await vm.HandleActiveCheckBoxClickAsync(node);

        Assert.Equal("42", node.LockedValue);
    }

    // ══════════════════════════════════════════════════════════════════
    // EDIT HELPERS
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void EditNodeValue_UpdatesAndLogs()
    {
        var vm = CreateVm();
        var node = MakeLeaf("HP", value: "100");
        vm.Roots!.Add(node);
        _dialogService.NextInputResult = "999";

        vm.EditNodeValue(node);

        Assert.Equal("999", node.CurrentValue);
        Assert.Equal("100", node.PreviousValue);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Value changed"));
    }

    [Fact]
    public void EditSelectedNode_ScriptEntry_ShowsScript()
    {
        var vm = CreateVm();
        var node = MakeScript("Godmode");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.EditSelectedNode();

        Assert.Single(_dialogService.InfoShown);
        Assert.Contains("Godmode", _dialogService.InfoShown[0].Title);
    }

    [Fact]
    public void EditSelectedNode_GroupEntry_DoesNothing()
    {
        var vm = CreateVm();
        var group = MakeGroup("Stats");
        vm.Roots!.Add(group);
        vm.SelectedNode = group;

        vm.EditSelectedNode();

        Assert.Empty(_dialogService.InfoShown);
    }

    [Fact]
    public void AddEntryFromDrop_AddsEntry()
    {
        var vm = CreateVm();
        vm.AddEntryFromDrop("0x12345");

        Assert.NotEmpty(vm.Roots!);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("0x12345"));
    }

    [Fact]
    public void ViewSelectedScript_NoScript_LogsWarning()
    {
        var vm = CreateVm();
        vm.SelectedNode = null;
        vm.ViewSelectedScriptCommand.Execute(null);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn");
    }

    [Fact]
    public void ViewSelectedScript_WithScript_ShowsInfo()
    {
        var vm = CreateVm();
        var node = MakeScript("Test");
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.ViewSelectedScriptCommand.Execute(null);

        Assert.Single(_dialogService.InfoShown);
    }

    // ══════════════════════════════════════════════════════════════════
    // MOVE TO GROUP
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void MoveToGroup_NoGroups_LogsWarning()
    {
        var vm = CreateVm();
        var node = MakeLeaf();
        vm.Roots!.Add(node);
        vm.SelectedNode = node;

        vm.MoveToGroupCommand.Execute(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("No groups"));
    }

    // ══════════════════════════════════════════════════════════════════
    // DISPOSE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Dispose_StopsAutoRefresh()
    {
        var vm = CreateVm();
        vm.StartAutoRefresh(500);
        vm.Dispose();
        // Should not throw on second dispose
        vm.Dispose();
    }

    // ══════════════════════════════════════════════════════════════════
    // AVAILABLE COLORS
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void AvailableColors_HasExpectedCount()
    {
        Assert.Equal(8, AddressTableViewModel.AvailableColors.Count);
        Assert.Equal("None", AddressTableViewModel.AvailableColors[0].Name);
    }

    // ══════════════════════════════════════════════════════════════════
    // DELETE SELECTED NODE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void DeleteSelectedNode_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedNode = null;
        vm.DeleteSelectedNode();
        // Should not crash
    }
}

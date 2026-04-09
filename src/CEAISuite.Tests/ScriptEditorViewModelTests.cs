using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ScriptEditorViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubAutoAssemblerEngine _assemblerEngine = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();

    private ScriptEditorViewModel CreateVm(IAutoAssemblerEngine? engine = null)
    {
        var addressTableService = new AddressTableService(_engineFacade);
        var scriptGenService = new ScriptGenerationService();
        return new ScriptEditorViewModel(
            addressTableService, engine ?? _assemblerEngine, scriptGenService, _processContext, _outputLog);
    }

    [Fact]
    public void NewScript_ClearsEditorAndResetsState()
    {
        var vm = CreateVm();
        vm.EditorText = "old content";

        vm.NewScriptCommand.Execute(null);

        Assert.Contains("[ENABLE]", vm.EditorText);
        Assert.Contains("[DISABLE]", vm.EditorText);
        Assert.False(vm.IsModified);
        Assert.Equal("New script", vm.StatusText);
    }

    [Fact]
    public void Validate_ValidScript_ShowsValid()
    {
        var vm = CreateVm();
        _assemblerEngine.NextParseResult = new ScriptParseResult(true, [], [], "[ENABLE]", "[DISABLE]");
        vm.EditorText = "[ENABLE]\nnop\n[DISABLE]\nnop";

        vm.ValidateCommand.Execute(null);

        Assert.Contains("Valid", vm.ValidationResult);
    }

    [Fact]
    public void Validate_InvalidScript_ShowsErrors()
    {
        var vm = CreateVm();
        _assemblerEngine.NextParseResult = new ScriptParseResult(false, ["Syntax error at line 1"], [], null, null);
        vm.EditorText = "garbage";

        vm.ValidateCommand.Execute(null);

        Assert.Contains("Errors", vm.ValidationResult);
        Assert.Contains("Syntax error", vm.ValidationResult);
    }

    [Fact]
    public void Validate_NoEngine_ShowsError()
    {
        var vm = CreateVm(engine: null);
        // Need to create with null engine — use a workaround
        var addressTableService = new AddressTableService(_engineFacade);
        var scriptGenService = new ScriptGenerationService();
        var vmNoEngine = new ScriptEditorViewModel(
            addressTableService, null, scriptGenService, _processContext, _outputLog);

        vmNoEngine.ValidateCommand.Execute(null);

        Assert.Contains("No assembler engine", vmNoEngine.ValidationResult);
    }

    [Fact]
    public async Task EnableScript_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.EnableScriptCommand.ExecuteAsync(null);

        Assert.Contains("No script loaded", vm.StatusText);
    }

    [Fact]
    public async Task EnableScript_NoEngine_SetsStatusError()
    {
        var addressTableService = new AddressTableService(_engineFacade);
        var scriptGenService = new ScriptGenerationService();
        var vm = new ScriptEditorViewModel(
            addressTableService, null, scriptGenService, _processContext, _outputLog);
        _processContext.AttachedProcessId = 1234;

        await vm.EnableScriptCommand.ExecuteAsync(null);

        Assert.Contains("No assembler engine", vm.StatusText);
    }

    [Fact]
    public void InsertTemplate_PrependsTemplateToEditor()
    {
        var vm = CreateVm();
        vm.EditorText = "// existing code";

        vm.InsertTemplateCommand.Execute("aob_inject");

        Assert.StartsWith("[ENABLE]", vm.EditorText);
        Assert.Contains("aobscanmodule", vm.EditorText);
        Assert.Contains("// existing code", vm.EditorText);
    }

    [Fact]
    public void SaveScript_NewScript_CreatesEntry()
    {
        var vm = CreateVm();
        vm.NewScriptCommand.Execute(null);
        vm.EditorText = "[ENABLE]\nnop\n[DISABLE]\nnop";

        vm.SaveScriptCommand.Execute(null);

        Assert.Contains("Script saved", vm.StatusText ?? "");
        Assert.False(vm.IsModified);
    }

    // ── Execute script (Enable/Disable) ──

    [Fact]
    public async Task EnableScript_WithLoadedScriptAndProcess_ExecutesSuccessfully()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        // Create, save, and load a script
        vm.NewScriptCommand.Execute(null);
        vm.EditorText = "[ENABLE]\nnop\n[DISABLE]\nnop";
        vm.SaveScriptCommand.Execute(null);

        _assemblerEngine.NextEnableResult = new ScriptExecutionResult(true, null, [], []);

        await vm.EnableScriptCommand.ExecuteAsync(null);

        Assert.Contains("Script enabled", vm.StatusText);
    }

    [Fact]
    public async Task EnableScript_FailedExecution_ShowsError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.NewScriptCommand.Execute(null);
        vm.EditorText = "[ENABLE]\nbad_instruction\n[DISABLE]\nnop";
        vm.SaveScriptCommand.Execute(null);

        _assemblerEngine.NextEnableResult = new ScriptExecutionResult(false, "Assembly failed at line 2", [], []);

        await vm.EnableScriptCommand.ExecuteAsync(null);

        Assert.Contains("Enable failed", vm.StatusText);
    }

    [Fact]
    public async Task DisableScript_NoLoadedScript_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        // No script loaded (loadedNodeId is null)

        await vm.DisableScriptCommand.ExecuteAsync(null);

        Assert.Contains("No script loaded", vm.StatusText);
    }

    [Fact]
    public async Task DisableScript_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        vm.NewScriptCommand.Execute(null);
        vm.EditorText = "[ENABLE]\nnop\n[DISABLE]\nnop";
        vm.SaveScriptCommand.Execute(null);

        await vm.DisableScriptCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task DisableScript_NoEngine_SetsStatusError()
    {
        var addressTableService = new AddressTableService(_engineFacade);
        var scriptGenService = new ScriptGenerationService();
        var vm = new ScriptEditorViewModel(
            addressTableService, null, scriptGenService, _processContext, _outputLog);
        _processContext.AttachedProcessId = 1234;

        await vm.DisableScriptCommand.ExecuteAsync(null);

        Assert.Contains("No assembler engine", vm.StatusText);
    }

    [Fact]
    public async Task DisableScript_WithLoadedScriptAndProcess_ExecutesSuccessfully()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.NewScriptCommand.Execute(null);
        vm.EditorText = "[ENABLE]\nnop\n[DISABLE]\nnop";
        vm.SaveScriptCommand.Execute(null);

        _assemblerEngine.NextDisableResult = new ScriptExecutionResult(true, null, [], []);

        await vm.DisableScriptCommand.ExecuteAsync(null);

        Assert.Contains("Script disabled", vm.StatusText);
    }

    [Fact]
    public async Task DisableScript_Failed_ShowsError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.NewScriptCommand.Execute(null);
        vm.EditorText = "[ENABLE]\nnop\n[DISABLE]\nnop";
        vm.SaveScriptCommand.Execute(null);

        _assemblerEngine.NextDisableResult = new ScriptExecutionResult(false, "Cannot restore original bytes", [], []);

        await vm.DisableScriptCommand.ExecuteAsync(null);

        Assert.Contains("Disable failed", vm.StatusText);
    }

    // ── Parse script / syntax validation ──

    [Fact]
    public void Validate_ValidWithWarnings_ShowsWarnings()
    {
        var vm = CreateVm();
        _assemblerEngine.NextParseResult = new ScriptParseResult(
            true, [], ["Unreachable code after jmp"], "[ENABLE]", "[DISABLE]");
        vm.EditorText = "[ENABLE]\njmp target\nnop\n[DISABLE]\nnop";

        vm.ValidateCommand.Execute(null);

        Assert.Contains("Valid", vm.ValidationResult);
        Assert.Contains("Warnings", vm.ValidationResult);
        Assert.Contains("Unreachable", vm.ValidationResult);
    }

    // ── EditorText modification tracking ──

    [Fact]
    public void EditorText_Change_SetsIsModified()
    {
        var vm = CreateVm();
        vm.NewScriptCommand.Execute(null);
        Assert.False(vm.IsModified);

        vm.EditorText = "new content";

        Assert.True(vm.IsModified);
    }

    // ── SaveScript for existing script ──

    [Fact]
    public void SaveScript_ExistingScript_UpdatesScript()
    {
        var vm = CreateVm();
        vm.NewScriptCommand.Execute(null);
        vm.EditorText = "[ENABLE]\nnop\n[DISABLE]\nnop";
        vm.SaveScriptCommand.Execute(null);

        // Modify the script and save again (same loadedNodeId)
        vm.EditorText = "[ENABLE]\nnop\nnop\n[DISABLE]\nnop";
        Assert.True(vm.IsModified);

        vm.SaveScriptCommand.Execute(null);

        Assert.False(vm.IsModified);
    }

    // ── DeleteScript ──

    [Fact]
    public void DeleteScript_NoLoadedScript_DoesNothing()
    {
        var vm = CreateVm();
        var previousStatus = vm.StatusText;

        vm.DeleteScriptCommand.Execute(null);

        // No change since no script was loaded
        Assert.Equal(previousStatus, vm.StatusText);
    }

    [Fact]
    public void DeleteScript_WithLoadedScript_RemovesScript()
    {
        var vm = CreateVm();
        vm.NewScriptCommand.Execute(null);
        vm.EditorText = "[ENABLE]\nnop\n[DISABLE]\nnop";
        vm.SaveScriptCommand.Execute(null);

        vm.DeleteScriptCommand.Execute(null);

        Assert.Contains("deleted", vm.StatusText);
        Assert.Equal("", vm.EditorText);
        Assert.False(vm.IsModified);
    }

    // ── InsertTemplate additional cases ──

    [Fact]
    public void InsertTemplate_CodeCave_PrependsTemplate()
    {
        var vm = CreateVm();
        vm.EditorText = "// existing";

        vm.InsertTemplateCommand.Execute("code_cave");

        Assert.StartsWith("[ENABLE]", vm.EditorText);
        Assert.Contains("cave", vm.EditorText);
    }

    [Fact]
    public void InsertTemplate_Nop_PrependsTemplate()
    {
        var vm = CreateVm();
        vm.EditorText = "";

        vm.InsertTemplateCommand.Execute("nop");

        Assert.Contains("db 90", vm.EditorText);
    }

    [Fact]
    public void InsertTemplate_Jmp_PrependsTemplate()
    {
        var vm = CreateVm();
        vm.EditorText = "";

        vm.InsertTemplateCommand.Execute("jmp");

        Assert.Contains("jmp", vm.EditorText);
    }

    [Fact]
    public void InsertTemplate_Unknown_PrependsNothing()
    {
        var vm = CreateVm();
        vm.EditorText = "code";

        vm.InsertTemplateCommand.Execute("unknown_type");

        Assert.Equal("code", vm.EditorText);
    }

    // ── RefreshScriptList ──

    [Fact]
    public void RefreshScriptList_SetsScriptCountStatus()
    {
        var vm = CreateVm();

        vm.RefreshScriptListCommand.Execute(null);

        Assert.Contains("script(s)", vm.StatusText);
    }

    // ── OpenScript ──

    [Fact]
    public void OpenScript_NonexistentId_DoesNothing()
    {
        var vm = CreateVm();
        var prevText = vm.EditorText;

        vm.OpenScript("nonexistent-id");

        Assert.Equal(prevText, vm.EditorText);
    }

    // ── LoadSelectedScript ──

    [Fact]
    public void LoadSelectedScript_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedScript = null;
        var prevText = vm.EditorText;

        vm.LoadSelectedScriptCommand.Execute(null);

        Assert.Equal(prevText, vm.EditorText);
    }
}

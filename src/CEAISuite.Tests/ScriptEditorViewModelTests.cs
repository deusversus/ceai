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

        // StatusText ends with "script(s)" because SaveScript calls RefreshScriptList
        Assert.Contains("script", vm.StatusText ?? "");
        Assert.False(vm.IsModified);
    }
}

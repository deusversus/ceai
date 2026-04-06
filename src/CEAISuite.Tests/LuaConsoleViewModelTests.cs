using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public sealed class LuaConsoleViewModelTests
{
    private readonly StubLuaScriptEngine _luaStub = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();

    private LuaConsoleViewModel CreateVm(bool withEngine = true) =>
        new(withEngine ? _luaStub : null, _processContext, _outputLog);

    [Fact]
    public async Task Execute_ValidScript_AddsOutputToHistory()
    {
        _luaStub.NextExecuteResult = new LuaExecutionResult(true, null, null, ["hello"]);
        var vm = CreateVm();
        vm.InputText = "print('hello')";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains(vm.History, e => e.Type == "input");
        Assert.Contains(vm.History, e => e.Type == "output" && e.Text == "hello");
    }

    [Fact]
    public async Task Execute_Error_AddsErrorToHistory()
    {
        _luaStub.NextExecuteResult = new LuaExecutionResult(false, null, "syntax error", []);
        var vm = CreateVm();
        vm.InputText = "bad code";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains(vm.History, e => e.Type == "error" && e.Text.Contains("syntax error"));
    }

    [Fact]
    public async Task Execute_ReturnValue_ShowsResult()
    {
        _luaStub.NextExecuteResult = new LuaExecutionResult(true, "42", null, []);
        var vm = CreateVm();
        vm.InputText = "return 42";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains(vm.History, e => e.Type == "result" && e.Text == "42");
    }

    [Fact]
    public async Task Execute_NoEngine_ShowsNotAvailable()
    {
        var vm = CreateVm(withEngine: false);
        vm.InputText = "print('test')";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains(vm.History, e => e.Type == "error" && e.Text.Contains("not available"));
    }

    [Fact]
    public async Task Execute_EmptyInput_DoesNothing()
    {
        var vm = CreateVm();
        vm.InputText = "";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Empty(vm.History);
    }

    [Fact]
    public async Task Execute_ClearsInputAfterRun()
    {
        _luaStub.NextExecuteResult = new LuaExecutionResult(true, null, null, []);
        var vm = CreateVm();
        vm.InputText = "print('test')";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.InputText);
    }

    [Fact]
    public void Clear_EmptiesHistory()
    {
        var vm = CreateVm();
        vm.History.Add(new("12:00:00", "input", "test"));
        vm.History.Add(new("12:00:00", "output", "result"));

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.History);
    }

    [Fact]
    public void ResetEngine_ClearsLuaState()
    {
        var vm = CreateVm();

        vm.ResetEngineCommand.Execute(null);

        Assert.Contains(vm.History, e => e.Text.Contains("reset"));
    }

    [Fact]
    public async Task EvaluateLine_Expression_ShowsResult()
    {
        _luaStub.NextEvaluateResult = new LuaExecutionResult(true, "7", null, []);
        var vm = CreateVm();
        vm.InputText = "1 + 2 * 3";

        await vm.EvaluateLineCommand.ExecuteAsync(null);

        Assert.Contains(vm.History, e => e.Type == "input" && e.Text.Contains("= 1 + 2 * 3"));
        Assert.Contains(vm.History, e => e.Type == "result" && e.Text == "7");
    }

    [Fact]
    public async Task Execute_WithProcess_UsesProcessId()
    {
        _processContext.Attach(new CEAISuite.Application.ProcessInspectionOverview(
            5678, "test.exe", "x64", [], null, null, null, "OK"));
        _luaStub.NextExecuteResult = new LuaExecutionResult(true, null, null, []);
        var vm = CreateVm();
        vm.InputText = "readInteger('0x1000')";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(5678, _luaStub.LastProcessId);
    }

    [Fact]
    public void IsEngineAvailable_WithEngine_True()
    {
        var vm = CreateVm(withEngine: true);
        Assert.True(vm.IsEngineAvailable);
    }

    [Fact]
    public void IsEngineAvailable_WithoutEngine_False()
    {
        var vm = CreateVm(withEngine: false);
        Assert.False(vm.IsEngineAvailable);
    }

    [Fact]
    public async Task Execute_MultiplePrintLines_AllCaptured()
    {
        _luaStub.NextExecuteResult = new LuaExecutionResult(true, null, null, ["line1", "line2", "line3"]);
        var vm = CreateVm();
        vm.InputText = "print('line1')\nprint('line2')\nprint('line3')";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.History.Count); // 1 input + 3 outputs
    }
}

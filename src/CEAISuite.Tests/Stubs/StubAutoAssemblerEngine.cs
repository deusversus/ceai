using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubAutoAssemblerEngine : IAutoAssemblerEngine
{
    public ScriptParseResult NextParseResult { get; set; } = new(true, [], [], "[ENABLE]", "[DISABLE]");
    public ScriptExecutionResult NextEnableResult { get; set; } = new(true, null, [], []);
    public ScriptExecutionResult NextDisableResult { get; set; } = new(true, null, [], []);

    public ScriptParseResult Parse(string script) => NextParseResult;

    public Task<ScriptExecutionResult> EnableAsync(int processId, string script, CancellationToken ct = default)
        => Task.FromResult(NextEnableResult);

    public Task<ScriptExecutionResult> DisableAsync(int processId, string script, CancellationToken ct = default)
        => Task.FromResult(NextDisableResult);
}

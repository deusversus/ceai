using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// AI-assisted scripting functions for Lua. These are CE AI Suite exclusives —
/// no equivalent in CE 7.5. Allows Lua scripts to invoke the AI operator for
/// code suggestions, function explanations, and pattern generation.
/// </summary>
internal static class LuaAiBindings
{
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        ILuaAiAssistant aiAssistant,
        IEngineFacade engineFacade,
        IAutoAssemblerEngine? autoAssembler)
    {
        // Create the ai.* namespace table
        var aiTable = new Table(script);

        // ai.suggest(context) → string
        // Ask the AI for a code suggestion given context about what the user is doing.
        aiTable["suggest"] = (Func<string, string>)(context =>
        {
            return aiAssistant.SuggestAsync(context).GetAwaiter().GetResult();
        });

        // ai.explain(address) → string
        // Ask the AI to analyze and explain what a function at an address does.
        aiTable["explain"] = (Func<DynValue, string>)(addrArg =>
        {
            var pid = LuaBindingHelpers.RequireProcess(engine);
            var address = LuaBindingHelpers.ResolveAddressArg(addrArg, pid, engineFacade, autoAssembler);
            return aiAssistant.ExplainAsync(address, pid).GetAwaiter().GetResult();
        });

        // ai.findPattern(description) → string
        // Ask the AI to generate an AOB pattern from a natural language description.
        aiTable["findPattern"] = (Func<string, string>)(description =>
        {
            var pid = LuaBindingHelpers.RequireProcess(engine);
            return aiAssistant.FindPatternAsync(description, pid).GetAwaiter().GetResult();
        });

        // ai.analyze(address, count?) → string
        // Quick disassembly + AI analysis of instructions at an address.
        aiTable["analyze"] = (Func<DynValue, DynValue, string>)((addrArg, countArg) =>
        {
            var pid = LuaBindingHelpers.RequireProcess(engine);
            var address = LuaBindingHelpers.ResolveAddressArg(addrArg, pid, engineFacade, autoAssembler);
            var context = $"Analyze the code at address 0x{(ulong)address:X} in process {pid}";
            return aiAssistant.SuggestAsync(context).GetAwaiter().GetResult();
        });

        script.Globals["ai"] = DynValue.NewTable(aiTable);
    }
}

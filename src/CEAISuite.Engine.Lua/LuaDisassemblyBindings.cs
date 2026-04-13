using System.Globalization;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible disassembly Lua functions: disassemble, getInstructionSize,
/// getPreviousOpcode, splitDisassembledString, assemble.
/// </summary>
internal static class LuaDisassemblyBindings
{
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        IDisassemblyEngine disassemblyEngine,
        IEngineFacade engineFacade,
        IAutoAssemblerEngine? autoAssembler)
    {
        // disassemble(address) → table {address, bytes, opcode, extra, size}
        script.Globals["disassemble"] = (Func<string, DynValue>)(addrExpr =>
        {
            var pid = RequireProcess(engine);
            var address = ResolveAddress(addrExpr, pid, engineFacade, autoAssembler);
            var result = disassemblyEngine.DisassembleAsync(pid, address, 1).GetAwaiter().GetResult();

            if (result.Instructions.Count == 0)
                return DynValue.Nil;

            var instr = result.Instructions[0];
            var table = new Table(script);
            table["address"] = FormatAddress(instr.Address);
            table["bytes"] = instr.HexBytes;
            table["opcode"] = instr.Mnemonic;
            table["extra"] = instr.Operands;
            table["size"] = (double)instr.Length;
            return DynValue.NewTable(table);
        });

        // getInstructionSize(address) → number
        script.Globals["getInstructionSize"] = (Func<string, double>)(addrExpr =>
        {
            var pid = RequireProcess(engine);
            var address = ResolveAddress(addrExpr, pid, engineFacade, autoAssembler);
            var result = disassemblyEngine.DisassembleAsync(pid, address, 1).GetAwaiter().GetResult();
            return result.Instructions.Count > 0 ? result.Instructions[0].Length : 0;
        });

        // getPreviousOpcode(address) → string (address of previous instruction)
        script.Globals["getPreviousOpcode"] = (Func<string, DynValue>)(addrExpr =>
        {
            var pid = RequireProcess(engine);
            var address = ResolveAddress(addrExpr, pid, engineFacade, autoAssembler);

            // Scan backward: disassemble from (address - 15) and find the instruction that ends at address
            var scanStart = address - 15;
            var result = disassemblyEngine.DisassembleAsync(pid, scanStart, 20).GetAwaiter().GetResult();

            for (int i = 0; i < result.Instructions.Count - 1; i++)
            {
                var next = result.Instructions[i + 1];
                if (next.Address == address)
                    return DynValue.NewString(FormatAddress(result.Instructions[i].Address));
            }

            return DynValue.Nil;
        });

        // splitDisassembledString(str) → table {address, bytes, opcode, extra}
        script.Globals["splitDisassembledString"] = (Func<string, DynValue>)(str =>
        {
            // CE format: "address - bytes - opcode extra"
            var parts = str.Split(" - ", 3, StringSplitOptions.TrimEntries);
            var table = new Table(script);
            table["address"] = parts.Length > 0 ? parts[0] : "";
            table["bytes"] = parts.Length > 1 ? parts[1] : "";

            if (parts.Length > 2)
            {
                var opcodeExtra = parts[2];
                var spaceIdx = opcodeExtra.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    table["opcode"] = opcodeExtra[..spaceIdx];
                    table["extra"] = opcodeExtra[(spaceIdx + 1)..];
                }
                else
                {
                    table["opcode"] = opcodeExtra;
                    table["extra"] = "";
                }
            }
            else
            {
                table["opcode"] = "";
                table["extra"] = "";
            }

            return DynValue.NewTable(table);
        });

        // assemble(instruction, address) → table of bytes
        if (autoAssembler is not null)
        {
            script.Globals["assemble"] = (Func<string, DynValue, DynValue>)((instruction, addrArg) =>
            {
                var pid = RequireProcess(engine);
                nuint address = 0;
                if (!addrArg.IsNil())
                    address = ResolveAddress(addrArg.CastToString() ?? "0", pid, engineFacade, autoAssembler);

                // Use AA engine to assemble a single instruction
                var aaScript = $"[ENABLE]\n{FormatAddress(address)}:\n  {instruction}\n[DISABLE]\n";
                var parseResult = autoAssembler.Parse(aaScript);
                if (!parseResult.IsValid)
                    throw new ScriptRuntimeException($"Assembly error: {string.Join("; ", parseResult.Errors)}");

                // Return success indicator — full byte extraction requires executing
                return DynValue.True;
            });
        }
    }

    private static int RequireProcess(MoonSharpLuaEngine engine)
    {
        return engine.CurrentProcessId
            ?? throw new ScriptRuntimeException("No process attached. Call openProcess() first.");
    }

    private static nuint ResolveAddress(
        string addrExpr, int pid, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        var resolved = LuaAddressResolver.ResolveAsync(addrExpr, pid, facade, aa).GetAwaiter().GetResult();
        return resolved ?? throw new ScriptRuntimeException($"Cannot resolve address: '{addrExpr}'");
    }

    private static string FormatAddress(nuint address)
        => $"0x{(ulong)address:X}";
}

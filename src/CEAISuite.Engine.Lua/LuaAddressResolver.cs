using System.Globalization;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Resolves CE-style address expressions used in getAddress():
/// hex ("0x1234"), module+offset ("game.dll+0x1A3F0"), registered symbols, pointer chains ("[game.dll+0x100]+0x10").
/// </summary>
internal static class LuaAddressResolver
{
    /// <summary>
    /// Resolve an address expression to a numeric address.
    /// Resolution order: hex literal → registered symbol → module+offset → pointer chain.
    /// </summary>
    public static async Task<nuint?> ResolveAsync(
        string expression,
        int processId,
        IEngineFacade engine,
        IAutoAssemblerEngine? autoAssembler,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var text = expression.Trim();

        // 1. Pointer chain: [[addr]+offset]+offset
        if (text.StartsWith('['))
            return await ResolvePointerChainAsync(text, processId, engine, autoAssembler, ct).ConfigureAwait(false);

        // 2. Plain hex: "0x1234" or "1234"
        if (TryParseHex(text, out var hexAddr))
            return (nuint)hexAddr;

        // 3. Registered symbol via AA engine
        if (autoAssembler is not null)
        {
            var symbolAddr = autoAssembler.ResolveSymbol(text);
            if (symbolAddr.HasValue)
                return symbolAddr.Value;
        }

        // 4. Module+offset: "game.dll+0x1A3F0"
        var plusIndex = text.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex > 0 && plusIndex < text.Length - 1)
        {
            var moduleName = text[..plusIndex].Trim();
            var offsetStr = text[(plusIndex + 1)..].Trim();

            if (TryParseHex(offsetStr, out var offset))
            {
                var moduleBase = await FindModuleBaseAsync(moduleName, processId, engine, ct).ConfigureAwait(false);
                if (moduleBase.HasValue)
                    return (nuint)((ulong)moduleBase.Value + offset);
            }
        }

        return null;
    }

    /// <summary>Resolve a pointer chain like "[[base]+0x10]+0x8".</summary>
    private static async Task<nuint?> ResolvePointerChainAsync(
        string expression,
        int processId,
        IEngineFacade engine,
        IAutoAssemblerEngine? autoAssembler,
        CancellationToken ct)
    {
        var state = new ParseState(expression);
        return await ResolveChainSegmentAsync(state, processId, engine, autoAssembler, ct).ConfigureAwait(false);
    }

    private sealed class ParseState(string expr)
    {
        public readonly string Expr = expr;
        public int Pos;
    }

    private static async Task<nuint?> ResolveChainSegmentAsync(
        ParseState state,
        int processId,
        IEngineFacade engine,
        IAutoAssemblerEngine? autoAssembler,
        CancellationToken ct)
    {
        SkipWhitespace(state);

        if (state.Pos < state.Expr.Length && state.Expr[state.Pos] == '[')
        {
            state.Pos++; // consume '['

            // Recurse for inner expression
            var innerAddr = await ResolveChainSegmentAsync(state, processId, engine, autoAssembler, ct).ConfigureAwait(false);
            if (!innerAddr.HasValue) return null;

            SkipWhitespace(state);

            // Handle +offset inside brackets
            if (state.Pos < state.Expr.Length && state.Expr[state.Pos] == '+')
            {
                state.Pos++; // consume '+'
                var offsetStr = ConsumeUntil(state, ']', '+');
                if (TryParseHex(offsetStr.Trim(), out var innerOffset))
                    innerAddr = (nuint)((ulong)innerAddr.Value + innerOffset);
            }

            // Consume closing bracket
            if (state.Pos < state.Expr.Length && state.Expr[state.Pos] == ']')
                state.Pos++;

            // Dereference: read pointer at the address
            var readResult = await engine.ReadValueAsync(processId, innerAddr.Value, MemoryDataType.Pointer, ct).ConfigureAwait(false);
            var ptrBytes = readResult.RawBytes;
            if (ptrBytes.Count < 8) return null;

            var derefAddr = (nuint)BitConverter.ToUInt64(ptrBytes is byte[] arr ? arr : ptrBytes.ToArray());

            // Handle +offset after closing bracket
            SkipWhitespace(state);
            if (state.Pos < state.Expr.Length && state.Expr[state.Pos] == '+')
            {
                state.Pos++; // consume '+'
                var outerOffsetStr = ConsumeUntil(state, ']', '+', '[');
                if (TryParseHex(outerOffsetStr.Trim(), out var outerOffset))
                    derefAddr = (nuint)((ulong)derefAddr + outerOffset);
            }

            return derefAddr;
        }
        else
        {
            // Base expression (no brackets) — resolve as address
            var baseExpr = ConsumeUntil(state, ']', '+');
            if (string.IsNullOrWhiteSpace(baseExpr))
                return null;

            return await ResolveAsync(baseExpr.Trim(), processId, engine, autoAssembler, ct).ConfigureAwait(false);
        }
    }

    internal static async Task<nuint?> FindModuleBaseAsync(
        string moduleName,
        int processId,
        IEngineFacade engine,
        CancellationToken ct)
    {
        // Re-attach to get module list (AttachAsync is idempotent for already-attached processes)
        try
        {
            var attachment = await engine.AttachAsync(processId, ct).ConfigureAwait(false);
            foreach (var mod in attachment.Modules)
            {
                if (mod.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                    return mod.BaseAddress;
            }
        }
        catch
        {
            // Swallow — module not found
        }

        return null;
    }

    internal static bool TryParseHex(string text, out ulong value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static void SkipWhitespace(ParseState s)
    {
        while (s.Pos < s.Expr.Length && char.IsWhiteSpace(s.Expr[s.Pos])) s.Pos++;
    }

    private static string ConsumeUntil(ParseState s, params char[] stopChars)
    {
        var start = s.Pos;
        while (s.Pos < s.Expr.Length && !stopChars.Contains(s.Expr[s.Pos])) s.Pos++;
        return s.Expr[start..s.Pos];
    }
}

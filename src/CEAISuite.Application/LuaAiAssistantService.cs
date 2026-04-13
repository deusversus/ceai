using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

/// <summary>
/// Implements <see cref="ILuaAiAssistant"/> by delegating to the configured AI chat client.
/// Provides AI-assisted scripting capabilities to Lua scripts — code suggestions,
/// function explanations, and AOB pattern generation.
/// </summary>
public sealed class LuaAiAssistantService : ILuaAiAssistant
{
    private readonly Func<IChatClient?> _chatClientFactory;
    private readonly IDisassemblyEngine? _disassemblyEngine;
    private readonly IEngineFacade? _engineFacade;
    private readonly ILogger<LuaAiAssistantService> _logger;

    public LuaAiAssistantService(
        Func<IChatClient?> chatClientFactory,
        ILogger<LuaAiAssistantService> logger,
        IDisassemblyEngine? disassemblyEngine = null,
        IEngineFacade? engineFacade = null)
    {
        _chatClientFactory = chatClientFactory;
        _logger = logger;
        _disassemblyEngine = disassemblyEngine;
        _engineFacade = engineFacade;
    }

    public async Task<string> SuggestAsync(string context, CancellationToken ct = default)
    {
        var client = _chatClientFactory();
        if (client is null)
            return "[AI not configured — set an API key in Settings]";

        try
        {
            var response = await client.GetResponseAsync(
                $"You are a game hacking assistant in CE AI Suite. Give a concise, actionable code suggestion for: {context}",
                cancellationToken: ct);
            return response.Text ?? "[No response]";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI suggest failed");
            return $"[AI error: {ex.Message}]";
        }
    }

    public async Task<string> ExplainAsync(nuint address, int processId, CancellationToken ct = default)
    {
        var client = _chatClientFactory();
        if (client is null)
            return "[AI not configured — set an API key in Settings]";

        // Gather disassembly context
        var disasmContext = "";
        if (_disassemblyEngine is not null)
        {
            try
            {
                var result = await _disassemblyEngine.DisassembleAsync(processId, address, 20, ct);
                var lines = result.Instructions.Select(i =>
                    $"  {(ulong)i.Address:X}: {i.HexBytes,-20} {i.Mnemonic} {i.Operands}");
                disasmContext = $"\n\nDisassembly at 0x{(ulong)address:X}:\n{string.Join("\n", lines)}";
            }
            catch { /* disassembly failed; proceed without it */ }
        }

        try
        {
            var response = await client.GetResponseAsync(
                $"You are a reverse engineering assistant. Explain what this function does in 2-3 sentences.{disasmContext}",
                cancellationToken: ct);
            return response.Text ?? "[No response]";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI explain failed");
            return $"[AI error: {ex.Message}]";
        }
    }

    public async Task<string> FindPatternAsync(string description, int processId, CancellationToken ct = default)
    {
        var client = _chatClientFactory();
        if (client is null)
            return "[AI not configured — set an API key in Settings]";

        try
        {
            var response = await client.GetResponseAsync(
                $"You are a game hacking assistant. Generate an x86-64 AOB (Array of Bytes) pattern for: {description}. " +
                "Return ONLY the hex pattern with ?? wildcards, no explanation. Example: 48 89 5C 24 ?? 48 83 EC 20",
                cancellationToken: ct);
            return response.Text?.Trim() ?? "[No response]";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI findPattern failed");
            return $"[AI error: {ex.Message}]";
        }
    }
}

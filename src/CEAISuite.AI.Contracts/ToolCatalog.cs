namespace CEAISuite.AI.Contracts;

public enum ToolRisk
{
    ReadOnly,
    WritesRequireConfirmation,
    HighRisk
}

public sealed record AiToolDefinition(
    string Name,
    string Category,
    string Purpose,
    ToolRisk Risk);

public static class ToolCatalog
{
    public static IReadOnlyList<AiToolDefinition> RequiredTools { get; } =
        new[]
        {
            new AiToolDefinition("list_processes", "Read/inspect", "Enumerate candidate target processes.", ToolRisk.ReadOnly),
            new AiToolDefinition("attach_process", "Read/inspect", "Bind the investigation session to a target process.", ToolRisk.WritesRequireConfirmation),
            new AiToolDefinition("read_memory", "Read/inspect", "Read typed memory values from the active target.", ToolRisk.ReadOnly),
            new AiToolDefinition("disassemble", "Read/inspect", "Inspect machine code around an address.", ToolRisk.ReadOnly),
            new AiToolDefinition("start_scan", "Search/analyze", "Begin a value or unknown-initial-state scan.", ToolRisk.ReadOnly),
            new AiToolDefinition("refine_scan", "Search/analyze", "Narrow an existing scan result set.", ToolRisk.ReadOnly),
            new AiToolDefinition("find_writes_to_address", "Search/analyze", "Trace writes reaching a selected address.", ToolRisk.ReadOnly),
            new AiToolDefinition("write_memory", "Modify", "Apply a typed value write to the target.", ToolRisk.WritesRequireConfirmation),
            new AiToolDefinition("patch_bytes", "Modify", "Patch machine code at a verified address.", ToolRisk.HighRisk),
            new AiToolDefinition("run_script", "Modify", "Execute an automation script against the current session.", ToolRisk.HighRisk),
            new AiToolDefinition("save_session", "Modify", "Persist the current investigation state.", ToolRisk.WritesRequireConfirmation),
            new AiToolDefinition("generate_aa_script", "Artifact generation", "Produce an Auto Assembler script from a validated workflow.", ToolRisk.ReadOnly),
            new AiToolDefinition("generate_lua_script", "Artifact generation", "Produce a reusable Lua automation helper.", ToolRisk.ReadOnly),
            new AiToolDefinition("summarize_investigation", "Artifact generation", "Capture findings, evidence, and next steps.", ToolRisk.ReadOnly)
        };
}

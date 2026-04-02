namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Global permission mode that determines the default tool access policy.
/// Checked as a fast-path in <see cref="PermissionEngine.Evaluate"/> before
/// individual rules are evaluated.
///
/// Modeled after Claude Code's permission modes: default, plan, acceptEdits,
/// bypassPermissions, dontAsk, auto, bubble.
/// </summary>
public enum PermissionMode
{
    /// <summary>
    /// Normal mode: dangerous tools require approval, rules are evaluated,
    /// everything else is allowed. Default behavior.
    /// </summary>
    Normal,

    /// <summary>
    /// Read-only mode: only tools marked with <see cref="ReadOnlyToolAttribute"/>
    /// are allowed. All other tools are auto-denied. Used during plan generation.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Unrestricted mode: all tools are auto-allowed without approval.
    /// Dangerous tool checks are bypassed. Use with caution.
    /// </summary>
    Unrestricted,

    /// <summary>
    /// Plan-only mode: only read-only tools plus planning meta-tools
    /// (plan_task, list_tool_categories, request_tools, get_budget_status,
    /// recall_memory, list_skills) are allowed. Used in plan mode.
    /// </summary>
    PlanOnly,
}

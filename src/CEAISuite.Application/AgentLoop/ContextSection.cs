namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// A typed section of dynamic context injected into the agent's system prompt
/// or user message. Sections are classified as cacheable (recomputed only when
/// invalidated) or volatile (recomputed every turn).
///
/// This replaces the flat string returned by the context provider with structured
/// sections that can be ordered for optimal prompt caching.
/// </summary>
public sealed record ContextSection
{
    /// <summary>Section name for identification.</summary>
    public required string Name { get; init; }

    /// <summary>The context content.</summary>
    public required string Content { get; init; }

    /// <summary>Whether this section can be cached across turns.</summary>
    public bool IsCacheable { get; init; }

    /// <summary>
    /// Priority for ordering within the prompt. Lower values appear first.
    /// Cacheable sections should have lower priority (placed first for prefix caching).
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>Optional injection format. Null = plain text in system prompt.</summary>
    public ContextInjectionFormat Format { get; init; } = ContextInjectionFormat.SystemPrompt;
}

/// <summary>Where and how context is injected into the conversation.</summary>
public enum ContextInjectionFormat
{
    /// <summary>Appended to the system prompt (default).</summary>
    SystemPrompt,

    /// <summary>
    /// Injected as a &lt;system-reminder&gt; block in the user message.
    /// Better for volatile context since it doesn't invalidate system prompt caching.
    /// </summary>
    SystemReminder,
}

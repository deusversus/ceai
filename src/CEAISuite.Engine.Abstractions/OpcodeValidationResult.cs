namespace CEAISuite.Engine.Abstractions;

/// <summary>
/// Result of opcode-level safety validation on assembled bytes.
/// Used to block dangerous instructions before they are written to process memory.
/// </summary>
public sealed record OpcodeValidationResult(
    bool IsValid,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    /// <summary>A passing result with no warnings or errors.</summary>
    public static OpcodeValidationResult Valid { get; } =
        new(true, Array.Empty<string>(), Array.Empty<string>());
}

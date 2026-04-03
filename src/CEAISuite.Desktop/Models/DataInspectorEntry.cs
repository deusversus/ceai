namespace CEAISuite.Desktop.Models;

/// <summary>A single row in the data inspector showing a typed interpretation of selected bytes.</summary>
public sealed record DataInspectorEntry(string TypeName, string Value, string HexValue);

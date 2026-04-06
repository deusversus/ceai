namespace CEAISuite.Desktop.Models;

/// <summary>Entry in the Lua console history — input, output, error, or result.</summary>
public sealed record LuaConsoleEntry(string Timestamp, string Type, string Text);

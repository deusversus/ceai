using System.Globalization;
using CEAISuite.Application;

namespace CEAISuite.Desktop.Services;

/// <summary>
/// Parses address expressions such as "0x00400000", "400000",
/// or "game.exe+0x1A3F0" (module+offset notation).
/// </summary>
public static class AddressExpressionParser
{
    /// <summary>
    /// Try to parse an address expression. Returns true on success.
    /// Supports: plain hex ("0x1234", "1234"), module+offset ("game.exe+0x1A3F0").
    /// </summary>
    public static bool TryParse(
        string expression,
        IReadOnlyList<ModuleOverview>? modules,
        out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var text = expression.Trim();

        // Check for module+offset pattern: "name+0xOFFSET" or "name+OFFSET"
        var plusIndex = text.IndexOf('+');
        if (plusIndex > 0 && plusIndex < text.Length - 1)
        {
            var moduleName = text[..plusIndex].Trim();
            var offsetStr = text[(plusIndex + 1)..].Trim();

            if (TryParseHex(offsetStr, out var offset))
            {
                if (modules is not null)
                {
                    foreach (var m in modules)
                    {
                        if (m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            // ModuleOverview.BaseAddress is a hex string like "0x00400000"
                            if (TryParseHex(m.BaseAddress, out var moduleBase))
                            {
                                address = moduleBase + offset;
                                return true;
                            }
                        }
                    }
                }
            }
        }

        // Plain hex: "0x1234" or "1234"
        return TryParseHex(text, out address);
    }

    private static bool TryParseHex(string text, out ulong value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

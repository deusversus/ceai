using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CEAISuite.Application;

/// <summary>
/// JSON converter for <see cref="nuint"/> that serializes as "0x..." hex strings
/// for human-readable pointer map files.
/// </summary>
public sealed class NuintJsonConverter : JsonConverter<nuint>
{
    public override nuint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        if (str is null) return 0;
        str = str.Trim();
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            str = str[2..];
        return (nuint)ulong.Parse(str, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, nuint value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"0x{value:X}");
    }
}

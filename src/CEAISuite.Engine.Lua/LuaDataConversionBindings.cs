using System.Buffers.Binary;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible data conversion Lua functions: byte table ↔ typed value converters
/// and legacy bitwise operations (bOr, bAnd, bXor, bShl, bShr, bNot).
/// </summary>
internal static class LuaDataConversionBindings
{
    public static void Register(Script script)
    {
        // ── To byte table ──
        script.Globals["wordToByteTable"] = (Func<double, DynValue>)(value =>
        {
            var bytes = new byte[2];
            unchecked { BinaryPrimitives.WriteInt16LittleEndian(bytes, (short)(long)value); }
            return BytesToTable(script, bytes);
        });

        script.Globals["dwordToByteTable"] = (Func<double, DynValue>)(value =>
        {
            var bytes = new byte[4];
            unchecked { BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)(long)value); }
            return BytesToTable(script, bytes);
        });

        script.Globals["qwordToByteTable"] = (Func<double, DynValue>)(value =>
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, (long)value);
            return BytesToTable(script, bytes);
        });

        script.Globals["floatToByteTable"] = (Func<double, DynValue>)(value =>
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)value);
            return BytesToTable(script, bytes);
        });

        script.Globals["doubleToByteTable"] = (Func<double, DynValue>)(value =>
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
            return BytesToTable(script, bytes);
        });

        script.Globals["stringToByteTable"] = (Func<string, DynValue>)(str =>
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(str);
            return BytesToTable(script, bytes);
        });

        script.Globals["wideStringToByteTable"] = (Func<string, DynValue>)(str =>
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(str);
            return BytesToTable(script, bytes);
        });

        // ── From byte table ──
        script.Globals["byteTableToWord"] = (Func<Table, double>)(table =>
        {
            var bytes = TableToBytes(table, 2);
            return BinaryPrimitives.ReadInt16LittleEndian(bytes);
        });

        script.Globals["byteTableToDword"] = (Func<Table, double>)(table =>
        {
            var bytes = TableToBytes(table, 4);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        });

        script.Globals["byteTableToQword"] = (Func<Table, double>)(table =>
        {
            var bytes = TableToBytes(table, 8);
            return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        });

        script.Globals["byteTableToFloat"] = (Func<Table, double>)(table =>
        {
            var bytes = TableToBytes(table, 4);
            return BinaryPrimitives.ReadSingleLittleEndian(bytes);
        });

        script.Globals["byteTableToDouble"] = (Func<Table, double>)(table =>
        {
            var bytes = TableToBytes(table, 8);
            return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        });

        script.Globals["byteTableToString"] = (Func<Table, string>)(table =>
        {
            var bytes = TableToBytes(table, table.Length);
            return System.Text.Encoding.ASCII.GetString(bytes);
        });

        script.Globals["byteTableToWideString"] = (Func<Table, string>)(table =>
        {
            var bytes = TableToBytes(table, table.Length);
            return System.Text.Encoding.Unicode.GetString(bytes);
        });

        // ── Legacy bitwise ops (CE compatibility — delegate to standard math) ──
        script.Globals["bOr"] = (Func<double, double, double>)((a, b) => (long)a | (long)b);
        script.Globals["bXor"] = (Func<double, double, double>)((a, b) => (long)a ^ (long)b);
        script.Globals["bAnd"] = (Func<double, double, double>)((a, b) => (long)a & (long)b);
        script.Globals["bShl"] = (Func<double, double, double>)((a, b) => (long)a << (int)(long)b);
        script.Globals["bShr"] = (Func<double, double, double>)((a, b) => (long)a >> (int)(long)b);
        script.Globals["bNot"] = (Func<double, double>)(a => ~(long)a);
    }

    private static DynValue BytesToTable(Script script, byte[] bytes)
    {
        var table = new Table(script);
        for (int i = 0; i < bytes.Length; i++)
            table[i + 1] = (double)bytes[i]; // 1-indexed
        return DynValue.NewTable(table);
    }

    private static byte[] TableToBytes(Table table, int expectedSize)
    {
        var bytes = new byte[Math.Min(table.Length, expectedSize)];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)table.Get(i + 1).Number;

        // Pad with zeros if table is shorter than expected
        if (bytes.Length < expectedSize)
        {
            var padded = new byte[expectedSize];
            bytes.CopyTo(padded, 0);
            return padded;
        }

        return bytes;
    }
}

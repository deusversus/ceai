using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible address list / memory record Lua functions.
/// CE's most-used scripting API: getAddressList, memoryrecord_getValue, etc.
/// Delegates to <see cref="ILuaAddressListProvider"/> for Application-layer isolation.
/// </summary>
internal static class LuaAddressListBindings
{
    public static void Register(Script script, ILuaAddressListProvider provider)
    {
        // getAddressList() → proxy table with Count property and methods
        script.Globals["getAddressList"] = (Func<DynValue>)(() =>
        {
            var al = new Table(script);
            al["Count"] = (double)provider.GetCount();
            al["getCount"] = (Func<double>)(() => provider.GetCount());

            al["getMemoryRecord"] = (Func<double, DynValue>)(index =>
            {
                var record = provider.GetRecord((int)index);
                return record is not null ? RecordToTable(script, record, provider) : DynValue.Nil;
            });

            al["getMemoryRecordByDescription"] = (Func<string, DynValue>)(desc =>
            {
                var record = provider.GetRecordByDescription(desc);
                return record is not null ? RecordToTable(script, record, provider) : DynValue.Nil;
            });

            al["getMemoryRecordByID"] = (Func<string, DynValue>)(id =>
            {
                var record = provider.GetRecordById(id);
                return record is not null ? RecordToTable(script, record, provider) : DynValue.Nil;
            });

            return DynValue.NewTable(al);
        });

        // Standalone convenience functions (CE scripts use both styles)
        script.Globals["addresslist_getCount"] = (Func<double>)(() => provider.GetCount());

        script.Globals["addresslist_getMemoryRecord"] = (Func<double, DynValue>)(index =>
        {
            var record = provider.GetRecord((int)index);
            return record is not null ? RecordToTable(script, record, provider) : DynValue.Nil;
        });

        script.Globals["addresslist_getMemoryRecordByDescription"] = (Func<string, DynValue>)(desc =>
        {
            var record = provider.GetRecordByDescription(desc);
            return record is not null ? RecordToTable(script, record, provider) : DynValue.Nil;
        });

        script.Globals["addresslist_getMemoryRecordByID"] = (Func<string, DynValue>)(id =>
        {
            var record = provider.GetRecordById(id);
            return record is not null ? RecordToTable(script, record, provider) : DynValue.Nil;
        });

        // memoryrecord_* standalone functions for CE compatibility
        script.Globals["memoryrecord_getValue"] = (Func<Table, DynValue>)(mr =>
        {
            var id = mr.Get("_id").String;
            var val = provider.GetValue(id);
            return val is not null ? DynValue.NewString(val) : DynValue.Nil;
        });

        script.Globals["memoryrecord_setValue"] = (Action<Table, string>)((mr, value) =>
            provider.SetValue(mr.Get("_id").String, value));

        script.Globals["memoryrecord_getAddress"] = (Func<Table, DynValue>)(mr =>
        {
            var addr = provider.GetAddress(mr.Get("_id").String);
            return addr is not null ? DynValue.NewString(addr) : DynValue.Nil;
        });

        script.Globals["memoryrecord_getDescription"] = (Func<Table, DynValue>)(mr =>
        {
            var desc = provider.GetDescription(mr.Get("_id").String);
            return desc is not null ? DynValue.NewString(desc) : DynValue.Nil;
        });

        script.Globals["memoryrecord_setDescription"] = (Action<Table, string>)((mr, desc) =>
            provider.SetDescription(mr.Get("_id").String, desc));

        script.Globals["memoryrecord_setActive"] = (Action<Table, bool>)((mr, active) =>
            provider.SetActive(mr.Get("_id").String, active));

        script.Globals["memoryrecord_getActive"] = (Func<Table, bool>)(mr =>
            provider.GetActive(mr.Get("_id").String));
    }

    private static DynValue RecordToTable(Script script, LuaMemoryRecord record, ILuaAddressListProvider provider)
    {
        var table = new Table(script);
        table["_id"] = record.Id;
        table["Description"] = record.Description;
        table["Address"] = record.Address;
        table["Type"] = record.DataType;
        table["Value"] = record.Value ?? "";
        table["Active"] = record.IsActive;
        table["IsGroupHeader"] = record.IsGroupHeader;

        // Method-style accessors (CE scripts call mr.getValue() or mr.Value)
        table["getValue"] = (Func<DynValue>)(() =>
        {
            var val = provider.GetValue(record.Id);
            return val is not null ? DynValue.NewString(val) : DynValue.Nil;
        });

        table["setValue"] = (Action<string>)(value =>
            provider.SetValue(record.Id, value));

        table["getAddress"] = (Func<string>)(() =>
            provider.GetAddress(record.Id) ?? record.Address);

        table["getDescription"] = (Func<string>)(() =>
            provider.GetDescription(record.Id) ?? record.Description);

        table["setDescription"] = (Action<string>)(desc =>
            provider.SetDescription(record.Id, desc));

        table["setActive"] = (Action<bool>)(active =>
            provider.SetActive(record.Id, active));

        table["getActive"] = (Func<bool>)(() =>
            provider.GetActive(record.Id));

        return DynValue.NewTable(table);
    }
}

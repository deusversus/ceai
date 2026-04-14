using System.Collections.Concurrent;
using CEAISuite.Engine.Lua;
using MoonSharp.Interpreter;
using Xunit;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for the CePropertyProxy metatable factory — the core of CE script compatibility.
/// Validates __index/__newindex behavior, case insensitivity, event routing, and backward compat.
/// </summary>
public sealed class CePropertyProxyTests
{
    private static Script CreateScript() => new(CoreModules.Preset_HardSandbox | CoreModules.Bit32);

    [Fact]
    public void Index_ReturnsLivePropertyValue()
    {
        var script = CreateScript();
        var table = new Table(script);
        var currentValue = "Hello";

        var props = CePropertyProxy.CreatePropertyMap();
        props["Caption"] = CePropertyProxy.ReadOnly(() => DynValue.NewString(currentValue));

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;
        var result = script.DoString("return obj.Caption");
        Assert.Equal("Hello", result.String);

        // Change the backing value — should read live
        currentValue = "World";
        result = script.DoString("return obj.Caption");
        Assert.Equal("World", result.String);
    }

    [Fact]
    public void NewIndex_CallsSetterForKnownProperty()
    {
        var script = CreateScript();
        var table = new Table(script);
        string? capturedValue = null;

        var props = CePropertyProxy.CreatePropertyMap();
        props["Caption"] = CePropertyProxy.ReadWrite(
            () => DynValue.NewString(capturedValue ?? ""),
            v => capturedValue = v.String);

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;
        script.DoString("obj.Caption = 'Hello'");
        Assert.Equal("Hello", capturedValue);
    }

    [Fact]
    public void NewIndex_SetterFiresOnEveryWrite_NotJustFirst()
    {
        // Critical invariant: known property names never rawset, so __newindex fires every time
        var script = CreateScript();
        var table = new Table(script);
        var callCount = 0;
        string? lastValue = null;

        var props = CePropertyProxy.CreatePropertyMap();
        props["Caption"] = CePropertyProxy.ReadWrite(
            () => DynValue.NewString(lastValue ?? ""),
            v => { lastValue = v.String; callCount++; });

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;
        script.DoString("obj.Caption = 'First'");
        Assert.Equal(1, callCount);
        Assert.Equal("First", lastValue);

        script.DoString("obj.Caption = 'Second'");
        Assert.Equal(2, callCount);
        Assert.Equal("Second", lastValue);

        script.DoString("obj.Caption = 'Third'");
        Assert.Equal(3, callCount);
        Assert.Equal("Third", lastValue);
    }

    [Fact]
    public void EventNames_RouteToCallbackRegistry()
    {
        var script = CreateScript();
        var table = new Table(script);
        var callbacks = new ConcurrentDictionary<string, DynValue>();
        var events = CePropertyProxy.CreateEventSet();
        events.Add("OnClick");
        events.Add("OnChange");

        CePropertyProxy.ApplyProxy(script, table, CePropertyProxy.CreatePropertyMap(),
            events, callbacks, "form1:btn1:");

        script.Globals["obj"] = table;
        script.DoString("obj.OnClick = function() end");

        Assert.True(callbacks.ContainsKey("form1:btn1:onclick"));

        // Setting nil removes the callback
        script.DoString("obj.OnClick = nil");
        Assert.False(callbacks.ContainsKey("form1:btn1:onclick"));
    }

    [Fact]
    public void UnknownKeys_FallThroughToRawTable()
    {
        var script = CreateScript();
        var table = new Table(script);

        CePropertyProxy.ApplyProxy(script, table, CePropertyProxy.CreatePropertyMap(),
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;

        // Custom user data should persist on the raw table
        script.DoString("obj.myData = 42");
        var result = script.DoString("return obj.myData");
        Assert.Equal(42, (int)result.Number);

        // Second write should also work (key now exists on raw table, no metatable involved)
        script.DoString("obj.myData = 99");
        result = script.DoString("return obj.myData");
        Assert.Equal(99, (int)result.Number);
    }

    [Fact]
    public void MethodsOnRawTable_TakePriorityOverProperties()
    {
        // Methods placed on the table BEFORE ApplyProxy should still resolve
        // because __index only fires when the key is absent from the raw table
        var script = CreateScript();
        var table = new Table(script);
        var methodCalled = false;

        // Place a method on the raw table
        table["setCaption"] = (Action<string>)(_ => methodCalled = true);

        var props = CePropertyProxy.CreatePropertyMap();
        // Even if there's a property named "setCaption", the raw table method wins
        props["setCaption"] = CePropertyProxy.ReadOnly(() => DynValue.NewString("should not see this"));

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;
        // Reading setCaption should return the function, not the property getter
        var result = script.DoString("return type(obj.setCaption)");
        Assert.Equal("function", result.String);

        script.DoString("obj.setCaption('test')");
        Assert.True(methodCalled);
    }

    [Fact]
    public void CaseInsensitivity_PropertyNames()
    {
        var script = CreateScript();
        var table = new Table(script);
        var capturedValue = "";

        var props = CePropertyProxy.CreatePropertyMap();
        props["Caption"] = CePropertyProxy.ReadWrite(
            () => DynValue.NewString(capturedValue),
            v => capturedValue = v.String);

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;

        // All casings should route to the same property
        script.DoString("obj.Caption = 'PascalCase'");
        Assert.Equal("PascalCase", capturedValue);

        script.DoString("obj.caption = 'lowercase'");
        Assert.Equal("lowercase", capturedValue);

        script.DoString("obj.CAPTION = 'UPPERCASE'");
        Assert.Equal("UPPERCASE", capturedValue);
    }

    [Fact]
    public void CaseInsensitivity_EventNames()
    {
        var script = CreateScript();
        var table = new Table(script);
        var callbacks = new ConcurrentDictionary<string, DynValue>();
        var events = CePropertyProxy.CreateEventSet();
        events.Add("OnClick");

        CePropertyProxy.ApplyProxy(script, table, CePropertyProxy.CreatePropertyMap(),
            events, callbacks, "test:");

        script.Globals["obj"] = table;

        // All casings should route to the same callback key
        script.DoString("obj.OnClick = function() end");
        Assert.True(callbacks.ContainsKey("test:onclick"));

        script.DoString("obj.onclick = function() end");
        Assert.True(callbacks.ContainsKey("test:onclick"));

        script.DoString("obj.ONCLICK = function() end");
        Assert.True(callbacks.ContainsKey("test:onclick"));
    }

    [Fact]
    public void ReadOnlyProperty_WritesAreSilentlyDiscarded()
    {
        var script = CreateScript();
        var table = new Table(script);

        var props = CePropertyProxy.CreatePropertyMap();
        props["Type"] = CePropertyProxy.ReadOnly(() => DynValue.NewString("Int32"));

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;

        // Read should return the live value
        var result = script.DoString("return obj.Type");
        Assert.Equal("Int32", result.String);

        // Write to a read-only property: silently discarded, does NOT rawset
        script.DoString("obj.Type = 'Float'");

        // Getter should still work — the raw table was NOT populated
        result = script.DoString("return obj.Type");
        Assert.Equal("Int32", result.String);
    }

    [Fact]
    public void ModelProp_CallsSetterAndHostUpdate()
    {
        var script = CreateScript();
        var table = new Table(script);
        var model = "initial";
        var hostUpdateCount = 0;

        var props = CePropertyProxy.CreatePropertyMap();
        props["Caption"] = CePropertyProxy.ModelProp(
            () => DynValue.NewString(model),
            v => model = v.String,
            () => hostUpdateCount++);

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;
        script.DoString("obj.Caption = 'updated'");

        Assert.Equal("updated", model);
        Assert.Equal(1, hostUpdateCount);
    }

    [Fact]
    public void ToDouble_HandlesNumberAndString()
    {
        Assert.Equal(42.0, CePropertyProxy.ToDouble(DynValue.NewNumber(42)));
        Assert.Equal(3.14, CePropertyProxy.ToDouble(DynValue.NewString("3.14")), 2);
        Assert.Equal(0, CePropertyProxy.ToDouble(DynValue.NewString("invalid")));
        Assert.Equal(0, CePropertyProxy.ToDouble(DynValue.Nil));
    }

    [Fact]
    public void ToBool_HandlesVariousTypes()
    {
        Assert.True(CePropertyProxy.ToBool(DynValue.True));
        Assert.False(CePropertyProxy.ToBool(DynValue.False));
        Assert.True(CePropertyProxy.ToBool(DynValue.NewNumber(1)));
        Assert.False(CePropertyProxy.ToBool(DynValue.NewNumber(0)));
        Assert.True(CePropertyProxy.ToBool(DynValue.NewString("true")));
        Assert.False(CePropertyProxy.ToBool(DynValue.NewString("false")));
        Assert.False(CePropertyProxy.ToBool(DynValue.NewString("0")));
        Assert.False(CePropertyProxy.ToBool(DynValue.Nil));
    }

    [Fact]
    public void SetterException_DoesNotCrashScript()
    {
        var script = CreateScript();
        var table = new Table(script);

        var props = CePropertyProxy.CreatePropertyMap();
        props["Broken"] = CePropertyProxy.ReadWrite(
            () => DynValue.NewString("ok"),
            _ => throw new InvalidOperationException("Intentional test failure"));

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;

        // Writing to a property whose setter throws should not crash the script
        script.DoString("obj.Broken = 'crash'");

        // Reading should still work
        var result = script.DoString("return obj.Broken");
        Assert.Equal("ok", result.String);
    }

    [Fact]
    public void GetterException_ReturnsNil()
    {
        var script = CreateScript();
        var table = new Table(script);

        var props = CePropertyProxy.CreatePropertyMap();
        props["Broken"] = CePropertyProxy.ReadOnly(
            () => throw new InvalidOperationException("Intentional test failure"));

        CePropertyProxy.ApplyProxy(script, table, props,
            CePropertyProxy.CreateEventSet(), new ConcurrentDictionary<string, DynValue>(), "test:");

        script.Globals["obj"] = table;

        // Reading a property whose getter throws should return nil, not crash
        var result = script.DoString("return obj.Broken");
        Assert.True(result.IsNil());
    }

    [Fact]
    public void EventRead_ReturnsStoredCallback()
    {
        var script = CreateScript();
        var table = new Table(script);
        var callbacks = new ConcurrentDictionary<string, DynValue>();
        var events = CePropertyProxy.CreateEventSet();
        events.Add("OnClick");

        CePropertyProxy.ApplyProxy(script, table, CePropertyProxy.CreatePropertyMap(),
            events, callbacks, "test:");

        script.Globals["obj"] = table;
        script.DoString("obj.OnClick = function() return 42 end");

        // Reading the event should return the stored function
        var result = script.DoString("return type(obj.OnClick)");
        Assert.Equal("function", result.String);
    }
}

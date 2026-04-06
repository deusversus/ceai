namespace CEAISuite.Engine.Abstractions;

/// <summary>Describes a Lua-created form to be rendered by the host (WPF layer).</summary>
public sealed record LuaFormDescriptor(
    string Id,
    string Title,
    int Width,
    int Height,
    List<LuaFormElement> Elements);

/// <summary>Base class for elements in a Lua-created form.</summary>
public abstract record LuaFormElement(string Id, string Type, int X, int Y, int Width, int Height)
{
    public string? Caption { get; set; }
}

public sealed record LuaButtonElement(string Id, int X, int Y, int Width, int Height)
    : LuaFormElement(Id, "button", X, Y, Width, Height);

public sealed record LuaLabelElement(string Id, int X, int Y, int Width, int Height)
    : LuaFormElement(Id, "label", X, Y, Width, Height);

public sealed record LuaEditElement(string Id, int X, int Y, int Width, int Height)
    : LuaFormElement(Id, "edit", X, Y, Width, Height)
{
    public string? Text { get; set; }
}

public sealed record LuaCheckBoxElement(string Id, int X, int Y, int Width, int Height)
    : LuaFormElement(Id, "checkbox", X, Y, Width, Height)
{
    public bool IsChecked { get; set; }
}

public sealed record LuaTimerElement(string Id, int IntervalMs)
    : LuaFormElement(Id, "timer", 0, 0, 0, 0)
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Host interface for rendering Lua-created forms. Implemented by the WPF layer.
/// The engine creates descriptors; the host renders and reports interactions.
/// </summary>
public interface ILuaFormHost
{
    void ShowForm(LuaFormDescriptor form);
    void CloseForm(string formId);
    void UpdateElement(string formId, LuaFormElement element);

    event Action<string, string>? ElementClicked;  // formId, elementId
    event Action<string, string>? TimerFired;       // formId, timerId
    event Action<string, string, string>? ElementTextChanged; // formId, elementId, text
}

namespace CEAISuite.Engine.Abstractions;

/// <summary>Describes a Lua-created form to be rendered by the host (WPF layer).</summary>
public sealed record LuaFormDescriptor(
    string Id,
    string Title,
    int Width,
    int Height,
    List<LuaFormElement> Elements);

/// <summary>Base class for elements in a Lua-created form. Mutable for position/size/caption updates.</summary>
public class LuaFormElement(string id, string type, int x, int y, int width, int height)
{
    public string Id { get; } = id;
    public string Type { get; } = type;
    public string? Caption { get; set; }
    public int X { get; set; } = x;
    public int Y { get; set; } = y;
    public int Width { get; set; } = width;
    public int Height { get; set; } = height;
}

public sealed class LuaButtonElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "button", x, y, width, height);

public sealed class LuaLabelElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "label", x, y, width, height);

public sealed class LuaEditElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "edit", x, y, width, height)
{
    public string? Text { get; set; }
}

public sealed class LuaCheckBoxElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "checkbox", x, y, width, height)
{
    public bool IsChecked { get; set; }
}

public sealed class LuaTimerElement(string id, int intervalMs)
    : LuaFormElement(id, "timer", 0, 0, 0, 0)
{
    public int IntervalMs { get; set; } = intervalMs;
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

    // Timer lifecycle
    void StartTimer(string formId, string timerId, int intervalMs);
    void StopTimer(string formId, string timerId);

    // Element value accessors (reads live WPF control state)
    string? GetElementText(string formId, string elementId);
    bool? GetElementChecked(string formId, string elementId);

    // Dialog functions
    void ShowMessageDialog(string text, string title);
    string? ShowInputDialog(string title, string prompt, string defaultValue);

    // Events
    event Action<string, string>? ElementClicked;  // formId, elementId
    event Action<string, string>? TimerFired;       // formId, timerId
    event Action<string, string, string>? ElementTextChanged; // formId, elementId, text
}

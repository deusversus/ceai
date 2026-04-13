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

    // S2: Common styling properties
    public bool Visible { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public string? FontName { get; set; }
    public int? FontSize { get; set; }
    public string? FontColor { get; set; }
    public string? BackColor { get; set; }
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
    public new bool Enabled { get; set; }
}

// ── Phase S2: Advanced GUI Elements ──

public sealed class LuaMemoElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "memo", x, y, width, height)
{
    public string? Text { get; set; }
    public bool ReadOnly { get; set; }
}

public sealed class LuaListBoxElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "listbox", x, y, width, height)
{
    public List<string> Items { get; set; } = [];
    public int SelectedIndex { get; set; } = -1;
}

public sealed class LuaComboBoxElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "combobox", x, y, width, height)
{
    public List<string> Items { get; set; } = [];
    public int SelectedIndex { get; set; } = -1;
}

public sealed class LuaTrackBarElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "trackbar", x, y, width, height)
{
    public int Min { get; set; }
    public int Max { get; set; } = 100;
    public int Position { get; set; }
}

public sealed class LuaProgressBarElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "progressbar", x, y, width, height)
{
    public int Min { get; set; }
    public int Max { get; set; } = 100;
    public int Position { get; set; }
}

public sealed class LuaImageElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "image", x, y, width, height)
{
    public string? ImagePath { get; set; }
}

public sealed class LuaPanelElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "panel", x, y, width, height);

public sealed class LuaGroupBoxElement(string id, int x, int y, int width, int height)
    : LuaFormElement(id, "groupbox", x, y, width, height);

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
    int? GetSelectedIndex(string formId, string elementId);
    int? GetTrackBarPosition(string formId, string elementId);

    // Dialog functions
    void ShowMessageDialog(string text, string title);
    string? ShowInputDialog(string title, string prompt, string defaultValue);

    // Canvas drawing
    void DrawLine(string formId, int x1, int y1, int x2, int y2, string color, int width);
    void DrawRect(string formId, int x1, int y1, int x2, int y2, string color, bool fill);
    void DrawEllipse(string formId, int x1, int y1, int x2, int y2, string color, bool fill);
    void DrawText(string formId, int x, int y, string text, string color, string? fontName, int? fontSize);
    void ClearCanvas(string formId);

    // Events
    event Action<string, string>? ElementClicked;  // formId, elementId
    event Action<string, string>? TimerFired;       // formId, timerId
    event Action<string, string, string>? ElementTextChanged; // formId, elementId, text
}

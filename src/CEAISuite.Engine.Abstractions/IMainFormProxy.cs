namespace CEAISuite.Engine.Abstractions;

/// <summary>
/// Proxy interface for Lua scripts to interact with the host application's main window.
/// Exposes panel visibility, navigation, status bar, theme, and menu extension.
/// Registered as a singleton in DI; consumed by <c>CeMainFormBindings</c> in Engine.Lua.
/// </summary>
public interface IMainFormProxy
{
    /// <summary>Returns whether the specified panel/document is currently visible.</summary>
    bool IsPanelVisible(string panelId);

    /// <summary>Shows or hides a panel by its ContentId.</summary>
    void SetPanelVisible(string panelId, bool visible);

    /// <summary>Returns the list of all known panel ContentIds.</summary>
    IReadOnlyList<string> GetPanelIds();

    /// <summary>Navigates the disassembler to the specified address (hex string).</summary>
    void NavigateDisassembler(string address);

    /// <summary>Navigates the memory browser to the specified address (hex string).</summary>
    void NavigateMemoryBrowser(string address);

    /// <summary>Sets the status bar text visible to scripts.</summary>
    void SetStatus(string text);

    /// <summary>Gets the current status bar text.</summary>
    string GetStatus();

    /// <summary>Returns true if the application is using a dark theme.</summary>
    bool IsDarkTheme();

    /// <summary>Returns the resolved theme name ("Dark" or "Light").</summary>
    string GetThemeName();

    /// <summary>Raised when the application theme changes. Argument is the new theme name.</summary>
    event Action<string>? ThemeChanged;

    /// <summary>
    /// Adds a menu item to the Scripts menu. Returns a unique menu item ID for removal.
    /// </summary>
    string AddMenuItem(string caption, Action callback);

    /// <summary>Removes a previously added menu item by its ID.</summary>
    void RemoveMenuItem(string menuItemId);
}

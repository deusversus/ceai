using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CEAISuite.Application;

/// <summary>
/// Manages system-wide global hotkeys using Windows RegisterHotKey API.
/// Each hotkey is associated with a callback action (e.g., toggle freeze, activate script).
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    public sealed record HotkeyBinding(int Id, uint Modifiers, uint VirtualKey, string Description, Action Callback);

    // Modifier key constants (matches Windows API)
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    private readonly ConcurrentDictionary<int, HotkeyBinding> _bindings = new();
    private int _nextId = 1;
    private IntPtr _hwnd;
    private bool _disposed;

    /// <summary>All currently registered hotkey bindings.</summary>
    public IReadOnlyCollection<HotkeyBinding> Bindings => _bindings.Values.ToList().AsReadOnly();

    /// <summary>
    /// Set the window handle used for hotkey registration.
    /// Must be called before registering hotkeys.
    /// </summary>
    public void SetWindowHandle(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>
    /// Register a global hotkey. Returns the binding ID, or -1 on failure.
    /// </summary>
    public int Register(uint modifiers, uint virtualKey, string description, Action callback)
    {
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Window handle not set. Call SetWindowHandle first.");

        var id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, virtualKey))
            return -1;

        _bindings[id] = new HotkeyBinding(id, modifiers, virtualKey, description, callback);
        return id;
    }

    /// <summary>Unregister a hotkey by its binding ID.</summary>
    public bool Unregister(int id)
    {
        if (!_bindings.TryRemove(id, out _)) return false;
        UnregisterHotKey(_hwnd, id);
        return true;
    }

    /// <summary>Unregister all hotkeys.</summary>
    public void UnregisterAll()
    {
        foreach (var id in _bindings.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _bindings.Clear();
    }

    /// <summary>
    /// Call this from the WndProc when WM_HOTKEY (0x0312) is received.
    /// </summary>
    public bool HandleHotkeyMessage(int hotkeyId)
    {
        if (_bindings.TryGetValue(hotkeyId, out var binding))
        {
            binding.Callback();
            return true;
        }
        return false;
    }

    /// <summary>Parse a hotkey string like "Ctrl+Shift+F1" into modifiers and virtual key code.</summary>
    public static (uint Modifiers, uint VirtualKey) ParseHotkeyString(string hotkey)
    {
        uint mods = 0;
        uint vk = 0;
        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mods |= MOD_CONTROL; break;
                case "ALT": mods |= MOD_ALT; break;
                case "SHIFT": mods |= MOD_SHIFT; break;
                case "WIN": mods |= MOD_WIN; break;
                default:
                    // Try F-keys
                    if (part.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(part[1..], out var fNum) && fNum is >= 1 and <= 24)
                    {
                        vk = (uint)(0x70 + fNum - 1); // VK_F1 = 0x70
                    }
                    // Single character
                    else if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        vk = (uint)char.ToUpperInvariant(part[0]);
                    }
                    // Numpad
                    else if (part.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(part[6..], out var numKey))
                    {
                        vk = (uint)(0x60 + numKey); // VK_NUMPAD0 = 0x60
                    }
                    break;
            }
        }

        return (mods, vk);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

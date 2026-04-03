using System.Windows.Media;

namespace CEAISuite.Desktop.Models;

/// <summary>A saved bookmark at a specific memory address.</summary>
public sealed class MemoryBookmark
{
    public ulong Address { get; set; }
    public string Label { get; set; } = "";
    public Color Color { get; set; } = Colors.Gold;
}

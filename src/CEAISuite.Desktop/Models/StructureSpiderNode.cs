using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CEAISuite.Desktop.Models;

/// <summary>A node in the structure spider tree — represents a pointer or field at an offset.</summary>
public partial class StructureSpiderNode : ObservableObject
{
    public StructureSpiderNode(ulong address, int offset, ulong pointsTo, string label, int depth = 0)
    {
        Address = address;
        Offset = offset;
        PointsTo = pointsTo;
        Label = label;
        Depth = depth;
    }

    public ulong Address { get; }
    public int Offset { get; }
    public ulong PointsTo { get; }
    public string Label { get; set; }
    public int Depth { get; }
    public string FieldType { get; set; } = "";

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<StructureSpiderNode> Children { get; } = [];
}

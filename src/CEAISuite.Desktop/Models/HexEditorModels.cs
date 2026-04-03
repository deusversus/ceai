namespace CEAISuite.Desktop.Models;

/// <summary>Represents a byte-level selection range in the hex editor.</summary>
public readonly record struct ByteSelection(int Start, int Length)
{
    public static readonly ByteSelection Empty = new(0, 0);

    public bool IsEmpty => Length == 0;
    public int End => Start + Length;

    /// <summary>Returns true if the given buffer offset falls within this selection.</summary>
    public bool Contains(int offset) => !IsEmpty && offset >= Start && offset < End;
}

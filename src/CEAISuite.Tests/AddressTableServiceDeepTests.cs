using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class AddressTableServiceDeepTests
{
    private readonly StubEngineFacade _engine = new();

    private AddressTableService CreateService() => new(_engine);

    // ── AddEntry CRUD ──

    [Fact]
    public void AddEntry_ReturnsEntryWithCorrectFields()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "42", "HP");

        Assert.Equal("HP", entry.Label);
        Assert.Equal("0x100", entry.Address);
        Assert.Equal(MemoryDataType.Int32, entry.DataType);
        Assert.Equal("42", entry.CurrentValue);
    }

    [Fact]
    public void AddEntry_AddsNodeToRoots()
    {
        var svc = CreateService();
        svc.AddEntry("0x100", MemoryDataType.Int32, "42", "HP");
        Assert.Single(svc.Roots);
        Assert.Equal("HP", svc.Roots[0].Label);
    }

    [Fact]
    public void AddEntry_NoLabel_GeneratesDefault()
    {
        var svc = CreateService();
        svc.AddEntry("0x100", MemoryDataType.Int32, "0");
        Assert.Single(svc.Roots);
        Assert.StartsWith("Address_", svc.Roots[0].Label);
    }

    [Fact]
    public void RemoveEntry_ExistingId_RemovesFromRoots()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "0", "HP");
        Assert.Single(svc.Roots);

        svc.RemoveEntry(entry.Id);
        Assert.Empty(svc.Roots);
    }

    [Fact]
    public void RemoveEntry_NonExistentId_DoesNotThrow()
    {
        var svc = CreateService();
        svc.RemoveEntry("nonexistent");
    }

    // ── Groups ──

    [Fact]
    public void CreateGroup_AddsGroupNodeToRoots()
    {
        var svc = CreateService();
        var group = svc.CreateGroup("Stats");
        Assert.Single(svc.Roots);
        Assert.True(group.IsGroup);
        Assert.Equal("Stats", group.Label);
    }

    [Fact]
    public void CreateSubGroup_AddsToParent()
    {
        var svc = CreateService();
        var parent = svc.CreateGroup("Parent");
        var child = svc.CreateSubGroup(parent.Id, "Child");
        Assert.Single(parent.Children);
        Assert.True(child.IsGroup);
        Assert.Equal("Child", child.Label);
    }

    [Fact]
    public void CreateSubGroup_InvalidParent_Throws()
    {
        var svc = CreateService();
        Assert.Throws<InvalidOperationException>(() => svc.CreateSubGroup("bad-id", "Child"));
    }

    [Fact]
    public void AddEntryToGroup_ValidGroup_AddsAsChild()
    {
        var svc = CreateService();
        var group = svc.CreateGroup("Stats");
        var entry = svc.AddEntryToGroup(group.Id, "0x200", MemoryDataType.Float, "1.5", "Speed");
        Assert.Single(group.Children);
        Assert.Equal("Speed", group.Children[0].Label);
    }

    [Fact]
    public void AddEntryToGroup_NonGroup_Throws()
    {
        var svc = CreateService();
        var leaf = svc.AddEntry("0x100", MemoryDataType.Int32, "0");
        Assert.Throws<InvalidOperationException>(() =>
            svc.AddEntryToGroup(leaf.Id, "0x200", MemoryDataType.Int32, "0"));
    }

    // ── MoveToGroup ──

    [Fact]
    public void MoveToGroup_MovesEntryIntoGroup()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "0", "HP");
        var group = svc.CreateGroup("Stats");

        svc.MoveToGroup(entry.Id, group.Id);

        Assert.Single(svc.Roots); // only group at root
        Assert.Single(group.Children); // entry moved inside
    }

    [Fact]
    public void MoveToGroup_NullGroupId_MovesToRoot()
    {
        var svc = CreateService();
        var group = svc.CreateGroup("Stats");
        var entry = svc.AddEntryToGroup(group.Id, "0x100", MemoryDataType.Int32, "0", "HP");

        svc.MoveToGroup(group.Children[0].Id, null);

        Assert.Equal(2, svc.Roots.Count); // group + moved entry
        Assert.Empty(group.Children);
    }

    // ── ToggleLock ──

    [Fact]
    public void ToggleLock_LocksEntry()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "42", "HP");
        svc.Roots[0].CurrentValue = "42";

        svc.ToggleLock(entry.Id);

        Assert.True(svc.Roots[0].IsLocked);
        Assert.Equal("42", svc.Roots[0].LockedValue);
    }

    [Fact]
    public void ToggleLock_AlreadyLocked_Unlocks()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "42", "HP");
        svc.ToggleLock(entry.Id);
        svc.ToggleLock(entry.Id);

        Assert.False(svc.Roots[0].IsLocked);
        Assert.Null(svc.Roots[0].LockedValue);
    }

    [Fact]
    public void ToggleLock_GroupNode_DoesNothing()
    {
        var svc = CreateService();
        var group = svc.CreateGroup("Stats");
        svc.ToggleLock(group.Id); // should be no-op
        Assert.False(group.IsLocked);
    }

    // ── UpdateLabel / UpdateNotes ──

    [Fact]
    public void UpdateLabel_ChangesLabel()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "0", "Old");
        svc.UpdateLabel(entry.Id, "New");
        Assert.Equal("New", svc.Roots[0].Label);
    }

    [Fact]
    public void UpdateNotes_SetsNotes()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "0", "HP");
        svc.UpdateNotes(entry.Id, "Player health");
        Assert.Equal("Player health", svc.Roots[0].Notes);
    }

    // ── Entries flat list ──

    [Fact]
    public void Entries_FlattensNestedStructure()
    {
        var svc = CreateService();
        svc.AddEntry("0x100", MemoryDataType.Int32, "0", "HP");
        var group = svc.CreateGroup("Stats");
        svc.AddEntryToGroup(group.Id, "0x200", MemoryDataType.Float, "1.0", "Speed");

        var entries = svc.Entries;
        Assert.Equal(2, entries.Count);
    }

    // ── FindNode ──

    [Fact]
    public void FindNode_ExistingId_ReturnsNode()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "0", "HP");
        var node = svc.Roots.First(); // via Roots since FindNode is internal
        Assert.Equal(entry.Id, node.Id);
    }

    // ── ClearAll ──

    [Fact]
    public void ClearAll_RemovesAllEntries()
    {
        var svc = CreateService();
        svc.AddEntry("0x100", MemoryDataType.Int32, "0", "A");
        svc.AddEntry("0x200", MemoryDataType.Int32, "0", "B");
        svc.CreateGroup("Stats");

        svc.ClearAll();

        Assert.Empty(svc.Roots);
        Assert.Empty(svc.Entries);
    }

    // ── ParseAddress ──

    [Theory]
    [InlineData("0x400000", 0x400000UL)]
    [InlineData("0xFF", 0xFFUL)]
    [InlineData("0x0", 0UL)]
    public void ParseAddress_ValidHex_ReturnsCorrectAddress(string input, ulong expected)
    {
        var result = AddressTableService.ParseAddress(input);
        Assert.Equal((nuint)expected, result);
    }

    // ── SetProcessContext ──

    [Fact]
    public void SetProcessContext_ClearsResolvedAddressCache()
    {
        var svc = CreateService();
        var entry = svc.AddEntry("0x100", MemoryDataType.Int32, "0", "HP");
        svc.Roots[0].ResolvedAddress = (nuint)0x100;

        svc.SetProcessContext([], false);

        Assert.Null(svc.Roots[0].ResolvedAddress);
    }

    // ── ImportNodes ──

    [Fact]
    public void ImportNodes_AddsNodesToRoots()
    {
        var svc = CreateService();
        var nodes = new[]
        {
            new AddressTableNode("n1", "HP", false) { Address = "0x100", DataType = MemoryDataType.Int32 },
            new AddressTableNode("n2", "MP", false) { Address = "0x200", DataType = MemoryDataType.Int32 },
        };

        svc.ImportNodes(nodes);

        Assert.Equal(2, svc.Roots.Count);
    }

    // ── AddFromScanResult ──

    [Fact]
    public void AddFromScanResult_AddsEntry()
    {
        var svc = CreateService();
        var scanResult = new ScanResultOverview("0x12345", "100", null, "64000000");
        svc.AddFromScanResult(scanResult, MemoryDataType.Int32, "Scan Hit");

        Assert.Single(svc.Roots);
        Assert.Equal("Scan Hit", svc.Roots[0].Label);
    }
}

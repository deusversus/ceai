using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests;

public sealed class HexEditUndoServiceTests
{
    [Fact]
    public void PushThenUndo_ReturnsOperationWithCorrectBytes()
    {
        var svc = new HexEditUndoService();
        var op = new HexEditOperation(0x400000, [0xAA], [0xBB]);
        svc.Push(op);

        var undone = svc.Undo();

        Assert.NotNull(undone);
        Assert.Equal(0x400000UL, undone.Address);
        Assert.Equal(new byte[] { 0xAA }, undone.OldBytes);
        Assert.Equal(new byte[] { 0xBB }, undone.NewBytes);
    }

    [Fact]
    public void PushThenUndoThenRedo_ReturnsSameOperation()
    {
        var svc = new HexEditUndoService();
        var op = new HexEditOperation(0x500000, [0x11], [0x22]);
        svc.Push(op);

        svc.Undo();
        var redone = svc.Redo();

        Assert.NotNull(redone);
        Assert.Equal(op, redone);
    }

    [Fact]
    public void UndoOnEmpty_ReturnsNull()
    {
        var svc = new HexEditUndoService();
        Assert.Null(svc.Undo());
    }

    [Fact]
    public void RedoOnEmpty_ReturnsNull()
    {
        var svc = new HexEditUndoService();
        Assert.Null(svc.Redo());
    }

    [Fact]
    public void PushAfterUndo_ClearsRedoStack()
    {
        var svc = new HexEditUndoService();
        svc.Push(new HexEditOperation(0x100, [0x01], [0x02]));
        svc.Undo();
        Assert.True(svc.CanRedo);

        svc.Push(new HexEditOperation(0x200, [0x03], [0x04]));
        Assert.False(svc.CanRedo);
    }

    [Fact]
    public void Clear_EmptiesBothStacks()
    {
        var svc = new HexEditUndoService();
        svc.Push(new HexEditOperation(0x100, [0x01], [0x02]));
        svc.Push(new HexEditOperation(0x200, [0x03], [0x04]));
        svc.Undo(); // move one to redo

        Assert.True(svc.CanUndo);
        Assert.True(svc.CanRedo);

        svc.Clear();

        Assert.False(svc.CanUndo);
        Assert.False(svc.CanRedo);
    }

    [Fact]
    public void CanUndo_ReflectsState()
    {
        var svc = new HexEditUndoService();
        Assert.False(svc.CanUndo);

        svc.Push(new HexEditOperation(0x100, [0x01], [0x02]));
        Assert.True(svc.CanUndo);

        svc.Undo();
        Assert.False(svc.CanUndo);
    }

    [Fact]
    public void CanRedo_ReflectsState()
    {
        var svc = new HexEditUndoService();
        Assert.False(svc.CanRedo);

        svc.Push(new HexEditOperation(0x100, [0x01], [0x02]));
        Assert.False(svc.CanRedo);

        svc.Undo();
        Assert.True(svc.CanRedo);

        svc.Redo();
        Assert.False(svc.CanRedo);
    }

    [Fact]
    public void MultipleOps_UndoInReverseOrder()
    {
        var svc = new HexEditUndoService();
        svc.Push(new HexEditOperation(0x100, [0x01], [0x02]));
        svc.Push(new HexEditOperation(0x200, [0x03], [0x04]));
        svc.Push(new HexEditOperation(0x300, [0x05], [0x06]));

        var third = svc.Undo();
        var second = svc.Undo();
        var first = svc.Undo();

        Assert.Equal(0x300UL, third!.Address);
        Assert.Equal(0x200UL, second!.Address);
        Assert.Equal(0x100UL, first!.Address);
    }
}

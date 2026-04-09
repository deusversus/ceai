using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.ViewModels;

namespace CEAISuite.Tests;

public class FindResultsViewModelTests
{
    private static FindResultsViewModel CreateVm() => new();

    [Fact]
    public void Populate_SetsResultsAndStatusText()
    {
        var vm = CreateVm();
        var items = new List<FindResultDisplayItem>
        {
            new() { Address = "0x1000", Instruction = "mov eax,1", Module = "game.dll", Context = "fn_start" },
            new() { Address = "0x2000", Instruction = "ret", Module = "game.dll", Context = "fn_end" }
        };

        vm.Populate(items, "search for mov");

        Assert.NotNull(vm.Results);
        Assert.Equal(2, vm.Results!.Count);
        Assert.Contains("2 results", vm.StatusText);
        Assert.Contains("search for mov", vm.StatusText);
    }

    [Fact]
    public void Clear_ResetsResultsAndStatusText()
    {
        var vm = CreateVm();
        vm.Populate(new List<FindResultDisplayItem>
        {
            new() { Address = "0x1000", Instruction = "nop", Module = "test.dll", Context = "" }
        }, "test");

        vm.ClearCommand.Execute(null);

        Assert.Null(vm.Results);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void Clear_WhenAlreadyEmpty_NoError()
    {
        var vm = CreateVm();

        vm.ClearCommand.Execute(null);

        Assert.Null(vm.Results);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void Populate_OverwritesPreviousResults()
    {
        var vm = CreateVm();
        vm.Populate(new List<FindResultDisplayItem>
        {
            new() { Address = "0x1000", Instruction = "nop", Module = "a.dll", Context = "" }
        }, "first");

        vm.Populate(new List<FindResultDisplayItem>
        {
            new() { Address = "0x2000", Instruction = "ret", Module = "b.dll", Context = "" },
            new() { Address = "0x3000", Instruction = "jmp", Module = "b.dll", Context = "" }
        }, "second");

        Assert.Equal(2, vm.Results!.Count);
        Assert.Contains("second", vm.StatusText);
    }
}

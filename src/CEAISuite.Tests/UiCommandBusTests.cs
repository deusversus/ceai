using CEAISuite.Application;

namespace CEAISuite.Tests;

public class UiCommandBusTests
{
    [Fact]
    public void Dispatch_WhitelistedCommand_RaisesEvent()
    {
        var bus = new UiCommandBus();
        UiCommand? received = null;
        bus.CommandReceived += cmd => received = cmd;

        var result = bus.Dispatch(new NavigatePanelCommand("scanner"));

        Assert.True(result);
        Assert.NotNull(received);
        Assert.IsType<NavigatePanelCommand>(received);
        Assert.Equal("scanner", ((NavigatePanelCommand)received).PanelId);
    }

    [Fact]
    public void Dispatch_NoSubscribers_ReturnsFalse()
    {
        var bus = new UiCommandBus();
        var result = bus.Dispatch(new NavigatePanelCommand("scanner"));
        Assert.False(result);
    }

    [Fact]
    public void GetWhitelist_ReturnsExpectedCommands()
    {
        var bus = new UiCommandBus();
        var whitelist = bus.GetWhitelist();
        Assert.Contains("NavigatePanel", whitelist);
        Assert.Contains("PopulateScanForm", whitelist);
        Assert.Contains("AddEntryToTable", whitelist);
        Assert.Contains("SetEntryValue", whitelist);
        Assert.Contains("AttachProcess", whitelist);
    }

    [Fact]
    public void ParseCommand_NavigatePanel_ReturnsCorrectType()
    {
        var cmd = UiCommandBus.ParseCommand("NavigatePanel", "{\"panelId\": \"scanner\"}");
        Assert.NotNull(cmd);
        Assert.IsType<NavigatePanelCommand>(cmd);
        Assert.Equal("scanner", ((NavigatePanelCommand)cmd).PanelId);
    }

    [Fact]
    public void ParseCommand_PopulateScanForm_ParsesAllFields()
    {
        var cmd = UiCommandBus.ParseCommand("PopulateScanForm",
            "{\"scanValue\": \"100\", \"scanType\": \"Exact\", \"dataType\": \"Float\"}");
        Assert.NotNull(cmd);
        var scan = Assert.IsType<PopulateScanFormCommand>(cmd);
        Assert.Equal("100", scan.ScanValue);
        Assert.Equal("Exact", scan.ScanType);
        Assert.Equal("Float", scan.DataType);
    }

    [Fact]
    public void ParseCommand_AddEntryToTable_ParsesAllFields()
    {
        var cmd = UiCommandBus.ParseCommand("AddEntryToTable",
            "{\"label\": \"HP\", \"address\": \"0x1234\", \"dataType\": \"Int32\", \"value\": \"100\"}");
        Assert.NotNull(cmd);
        var add = Assert.IsType<AddEntryToTableCommand>(cmd);
        Assert.Equal("HP", add.Label);
        Assert.Equal("0x1234", add.Address);
        Assert.Equal("100", add.Value);
    }

    [Fact]
    public void ParseCommand_SetEntryValue_ParsesFields()
    {
        var cmd = UiCommandBus.ParseCommand("SetEntryValue",
            "{\"entryId\": \"n_abc\", \"newValue\": \"999\"}");
        Assert.NotNull(cmd);
        var set = Assert.IsType<SetEntryValueCommand>(cmd);
        Assert.Equal("n_abc", set.EntryId);
        Assert.Equal("999", set.NewValue);
    }

    [Fact]
    public void ParseCommand_AttachProcess_ParsesPid()
    {
        var cmd = UiCommandBus.ParseCommand("AttachProcess", "{\"processId\": 1234}");
        Assert.NotNull(cmd);
        var attach = Assert.IsType<AttachProcessCommand>(cmd);
        Assert.Equal(1234, attach.ProcessId);
    }

    [Fact]
    public void ParseCommand_Unknown_ReturnsNull()
    {
        var cmd = UiCommandBus.ParseCommand("DeleteEverything", "{}");
        Assert.Null(cmd);
    }

    [Fact]
    public void ParseCommand_InvalidJson_ReturnsNull()
    {
        var cmd = UiCommandBus.ParseCommand("NavigatePanel", "not json at all");
        Assert.Null(cmd);
    }

    [Fact]
    public void ParseCommand_CaseInsensitive()
    {
        var cmd = UiCommandBus.ParseCommand("navigatepanel", "{\"panelId\": \"scanner\"}");
        Assert.NotNull(cmd);
        Assert.IsType<NavigatePanelCommand>(cmd);
    }

    [Fact]
    public void Dispatch_MultipleSubscribers_AllReceive()
    {
        var bus = new UiCommandBus();
        int count = 0;
        bus.CommandReceived += _ => count++;
        bus.CommandReceived += _ => count++;

        bus.Dispatch(new NavigatePanelCommand("scanner"));
        Assert.Equal(2, count);
    }

    [Fact]
    public void Dispatch_CoPilotDisabled_ReturnsFalse()
    {
        using var settings = new AppSettingsService();
        settings.Settings.EnableCoPilot = false;
        var bus = new UiCommandBus(settings);
        UiCommand? received = null;
        bus.CommandReceived += cmd => received = cmd;

        var result = bus.Dispatch(new NavigatePanelCommand("scanner"));

        Assert.False(result);
        Assert.Null(received); // Event should not fire when disabled
    }

    [Fact]
    public void Dispatch_CoPilotEnabled_Succeeds()
    {
        using var settings = new AppSettingsService();
        settings.Settings.EnableCoPilot = true;
        var bus = new UiCommandBus(settings);
        UiCommand? received = null;
        bus.CommandReceived += cmd => received = cmd;

        var result = bus.Dispatch(new NavigatePanelCommand("scanner"));

        Assert.True(result);
        Assert.NotNull(received);
    }

    [Fact]
    public void Dispatch_NonWhitelistedCommand_ReturnsFalse()
    {
        var bus = new UiCommandBus();
        bus.CommandReceived += _ => { };

        var result = bus.Dispatch(new FakeNonWhitelistedCommand());
        Assert.False(result);
    }

    private sealed record FakeNonWhitelistedCommand() : UiCommand("DeleteEverything");
}

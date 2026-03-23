using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests.Stubs;

public sealed class StubDialogService : IDialogService
{
    public string? NextInputResult { get; set; }
    public bool NextConfirmResult { get; set; } = true;
    public string? NextSaveFilePath { get; set; }
    public string? NextOpenFilePath { get; set; }
    public string[]? NextOpenFilesPaths { get; set; }

    public List<(string Title, string Message)> InfoShown { get; } = new();
    public List<(string Title, string Message)> ErrorsShown { get; } = new();
    public List<(string Title, string Message)> WarningsShown { get; } = new();
    public List<(string Title, string Message)> ConfirmsShown { get; } = new();

    public string? ShowInput(string title, string prompt, string defaultValue = "") => NextInputResult;
    public bool Confirm(string title, string message) { ConfirmsShown.Add((title, message)); return NextConfirmResult; }
    public void ShowInfo(string title, string message) => InfoShown.Add((title, message));
    public void ShowError(string title, string message) => ErrorsShown.Add((title, message));
    public void ShowWarning(string title, string message) => WarningsShown.Add((title, message));
    public string? ShowSaveFileDialog(string filter, string defaultName = "") => NextSaveFilePath;
    public string? ShowOpenFileDialog(string filter) => NextOpenFilePath;
    public string[]? ShowOpenFilesDialog(string filter) => NextOpenFilesPaths;
}

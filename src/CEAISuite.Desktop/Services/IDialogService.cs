namespace CEAISuite.Desktop.Services;

public interface IDialogService
{
    string? ShowInput(string title, string prompt, string defaultValue = "");
    bool Confirm(string title, string message);
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
    string? ShowSaveFileDialog(string filter, string defaultName = "");
    string? ShowOpenFileDialog(string filter);
}

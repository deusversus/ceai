using System.Windows.Controls;
using System.Windows.Input;
using CEAISuite.Desktop.Controls;
using CEAISuite.Desktop.ViewModels;

namespace CEAISuite.Desktop;

public partial class MemoryBrowserControl : UserControl
{
    private MemoryBrowserViewModel? _viewModel;

    public MemoryBrowserControl()
    {
        InitializeComponent();
        HexEditor.ByteEdited += OnByteEdited;
        HexEditor.PreviewKeyDown += OnHexEditorKeyDown;
    }

    /// <summary>Bind the ViewModel (called once from MainWindow after DI resolution).</summary>
    public void SetViewModel(MemoryBrowserViewModel vm)
    {
        _viewModel = vm;
        DataContext = vm;
    }

    /// <summary>Bind the control to an attached process and optionally navigate to an address.</summary>
    public void AttachProcess(nuint initialAddress = 0)
    {
        _viewModel?.AttachProcess(initialAddress);
    }

    /// <summary>Clear the display and detach from the current process.</summary>
    public void Clear()
    {
        _viewModel?.Clear();
    }

    /// <summary>Navigate to the specified address and refresh the display.</summary>
    public async Task NavigateToAddress(nuint address)
    {
        if (_viewModel is not null)
            await _viewModel.NavigateToAddressAsync(address);
    }

    private async void OnByteEdited(object? sender, ByteEditedEventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.HandleByteEditedAsync(e);
    }

    private async void OnHexEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        switch (e.Key)
        {
            case Key.C:
                _viewModel.CopyHexCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.V:
                await _viewModel.PasteHexCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
        }
    }
}

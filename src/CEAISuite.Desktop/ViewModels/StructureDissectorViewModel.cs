using System.Collections.ObjectModel;
using System.Text;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class StructureDissectorViewModel : ObservableObject
{
    private readonly StructureDissectorService _dissectorService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;

    public StructureDissectorViewModel(
        StructureDissectorService dissectorService,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _dissectorService = dissectorService;
        _processContext = processContext;
        _outputLog = outputLog;
    }

    [ObservableProperty] private string _baseAddress = "";
    [ObservableProperty] private int _regionSize = 256;
    [ObservableProperty] private string _selectedTypeHint = "auto";
    [ObservableProperty] private ObservableCollection<StructureFieldDisplayItem> _fields = new();
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isDissecting;

    public IReadOnlyList<string> TypeHints { get; } = ["auto", "int32", "float", "pointers"];

    [RelayCommand]
    private async Task DissectAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        if (string.IsNullOrWhiteSpace(BaseAddress)) { StatusText = "Enter a base address."; return; }

        if (!TryParseAddress(BaseAddress, out var addr)) { StatusText = "Invalid address."; return; }

        IsDissecting = true;
        StatusText = "Dissecting...";
        try
        {
            var (result, clusters) = await _dissectorService.DissectAsync(
                pid.Value, addr, RegionSize, SelectedTypeHint);

            Fields.Clear();
            foreach (var f in result)
            {
                Fields.Add(new StructureFieldDisplayItem
                {
                    Offset = f.Offset,
                    ProbableType = f.ProbableType,
                    DisplayValue = f.DisplayValue,
                    Confidence = f.Confidence
                });
            }
            StatusText = $"{Fields.Count} fields, {clusters} cluster(s) detected";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _outputLog.Append("Dissector", "Error", ex.Message);
        }
        finally { IsDissecting = false; }
    }

    [RelayCommand]
    private void FollowPointer()
    {
        if (_selectedField is null || _selectedField.ProbableType != "Pointer") return;
        var sel = _selectedField;
        BaseAddress = sel.DisplayValue;
        _ = DissectAsync();
    }

    [ObservableProperty] private StructureFieldDisplayItem? _selectedField;

    [RelayCommand]
    private void ExportCStruct()
    {
        if (Fields.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("struct Unknown {");
        foreach (var f in Fields)
        {
            var cType = f.ProbableType switch
            {
                "Int32" => "int32_t",
                "Int64" => "int64_t",
                "Float" => "float",
                "Double" => "double",
                "Pointer" => "void*",
                _ => "uint8_t[4]"
            };
            var name = string.IsNullOrWhiteSpace(f.Name) ? $"field_{f.Offset:X3}" : f.Name;
            sb.AppendLine($"    {cType} {name}; // offset {f.OffsetHex}, confidence {f.ConfidencePercent}");
        }
        sb.AppendLine("};");
        System.Windows.Clipboard.SetText(sb.ToString());
        StatusText = "C struct copied to clipboard.";
    }

    /// <summary>Navigate to a specific address for dissection (called externally).</summary>
    public void NavigateToAddress(string address)
    {
        BaseAddress = address;
        _ = DissectAsync();
    }

    private static bool TryParseAddress(string text, out nuint address)
    {
        address = 0;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return nuint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}

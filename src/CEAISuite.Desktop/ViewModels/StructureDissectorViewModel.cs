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
    private readonly IClipboardService _clipboard;

    public StructureDissectorViewModel(
        StructureDissectorService dissectorService,
        IProcessContext processContext,
        IOutputLog outputLog,
        IClipboardService clipboard)
    {
        _dissectorService = dissectorService;
        _processContext = processContext;
        _outputLog = outputLog;
        _clipboard = clipboard;
    }

    [ObservableProperty] private string _baseAddress = "";
    [ObservableProperty] private int _regionSize = 256;
    [ObservableProperty] private string _selectedTypeHint = "auto";
    [ObservableProperty] private ObservableCollection<StructureFieldDisplayItem> _fields = new();
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isDissecting;

    // ── Side-by-side compare ──
    [ObservableProperty] private string _compareAddress = "";
    [ObservableProperty] private ObservableCollection<StructureCompareDisplayItem> _compareResults = new();
    [ObservableProperty] private bool _isComparing;

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
        _clipboard.SetText(sb.ToString());
        StatusText = "C struct copied to clipboard.";
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        if (string.IsNullOrWhiteSpace(BaseAddress) || string.IsNullOrWhiteSpace(CompareAddress))
        { StatusText = "Enter both base and compare addresses."; return; }
        if (!TryParseAddress(BaseAddress, out var addrA) || !TryParseAddress(CompareAddress, out var addrB))
        { StatusText = "Invalid address."; return; }

        IsComparing = true;
        StatusText = "Comparing...";
        try
        {
            var (fieldsA, _) = await _dissectorService.DissectAsync(pid.Value, addrA, RegionSize, SelectedTypeHint);
            var (fieldsB, _) = await _dissectorService.DissectAsync(pid.Value, addrB, RegionSize, SelectedTypeHint);

            var dictB = fieldsB.ToDictionary(f => f.Offset);
            CompareResults.Clear();
            foreach (var fA in fieldsA)
            {
                var valueB = dictB.TryGetValue(fA.Offset, out var fB) ? fB.DisplayValue : "—";
                var differs = !string.Equals(fA.DisplayValue, valueB, StringComparison.Ordinal);
                CompareResults.Add(new StructureCompareDisplayItem
                {
                    OffsetHex = $"0x{fA.Offset:X3}",
                    Type = fA.ProbableType,
                    ValueA = fA.DisplayValue,
                    ValueB = valueB,
                    IsDifferent = differs
                });
            }
            StatusText = $"{CompareResults.Count} fields compared, {CompareResults.Count(r => r.IsDifferent)} differ";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally { IsComparing = false; }
    }

    [RelayCommand]
    private void ExportCEStruct()
    {
        if (Fields.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<Structure Name=\"Unknown\" AutoFill=\"0\" AutoCreate=\"1\" DefaultHex=\"1\" AutoDestroy=\"0\" DoNotSaveLocal=\"0\" AutoCreateStructsize=\"4096\">");
        foreach (var f in Fields)
        {
            var ceType = f.ProbableType switch
            {
                "Int32" => "4 Bytes",
                "Int64" => "8 Bytes",
                "Float" => "Float",
                "Double" => "Double",
                "Pointer" => "Pointer",
                _ => "4 Bytes"
            };
            var name = string.IsNullOrWhiteSpace(f.Name) ? $"field_{f.Offset:X3}" : f.Name;
            sb.AppendLine($"  <Element Offset=\"{f.Offset}\" Vartype=\"{ceType}\" Bytesize=\"{GetByteSize(f.ProbableType)}\" Description=\"{name}\" DisplayMethod=\"0\"/>");
        }
        sb.AppendLine("</Structure>");
        _clipboard.SetText(sb.ToString());
        StatusText = "CE structure definition copied to clipboard.";
    }

    private static int GetByteSize(string type) => type switch
    {
        "Int64" or "Double" or "Pointer" => 8,
        _ => 4
    };

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

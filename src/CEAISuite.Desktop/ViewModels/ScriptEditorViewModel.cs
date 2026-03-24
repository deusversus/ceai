using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class ScriptEditorViewModel : ObservableObject
{
    private readonly AddressTableService _addressTableService;
    private readonly IAutoAssemblerEngine? _autoAssemblerEngine;
    private readonly ScriptGenerationService _scriptGenService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private string? _loadedNodeId;

    public ScriptEditorViewModel(
        AddressTableService addressTableService,
        IAutoAssemblerEngine? autoAssemblerEngine,
        ScriptGenerationService scriptGenService,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _addressTableService = addressTableService;
        _autoAssemblerEngine = autoAssemblerEngine;
        _scriptGenService = scriptGenService;
        _processContext = processContext;
        _outputLog = outputLog;
        RefreshScriptList();
    }

    [ObservableProperty] private ObservableCollection<ScriptDisplayItem> _scripts = new();
    [ObservableProperty] private ScriptDisplayItem? _selectedScript;
    [ObservableProperty] private string _editorText = "";
    [ObservableProperty] private string? _validationResult;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isModified;

    partial void OnEditorTextChanged(string value) => IsModified = true;

    [RelayCommand]
    private void RefreshScriptList()
    {
        Scripts.Clear();
        foreach (var node in _addressTableService.Roots)
            CollectScripts(node);
        StatusText = $"{Scripts.Count} script(s)";
    }

    private void CollectScripts(AddressTableNode node)
    {
        if (node.IsScriptEntry)
        {
            Scripts.Add(new ScriptDisplayItem
            {
                Id = node.Id,
                Label = node.Label,
                IsEnabled = node.IsScriptEnabled,
                StatusText = node.ScriptStatus ?? ""
            });
        }
        foreach (var child in node.Children)
            CollectScripts(child);
    }

    [RelayCommand]
    private void LoadSelectedScript()
    {
        if (SelectedScript is null) return;
        var node = FindNode(SelectedScript.Id);
        if (node?.AssemblerScript is null) return;
        EditorText = node.AssemblerScript;
        _loadedNodeId = node.Id;
        IsModified = false;
        StatusText = $"Loaded: {node.Label}";
        ValidationResult = null;
    }

    [RelayCommand]
    private void NewScript()
    {
        EditorText = "[ENABLE]\n// Write your script here\n\n[DISABLE]\n// Restore original code here\n";
        _loadedNodeId = null;
        IsModified = false;
        StatusText = "New script";
        ValidationResult = null;
    }

    [RelayCommand]
    private void SaveScript()
    {
        if (_loadedNodeId is null)
        {
            // Create new script entry
            var entry = _addressTableService.AddEntry("0", Engine.Abstractions.MemoryDataType.Byte, "0", "New Script");
            var node = FindNode(entry.Id);
            if (node is not null)
            {
                node.AssemblerScript = EditorText;
                _loadedNodeId = node.Id;
            }
        }
        else
        {
            var node = FindNode(_loadedNodeId);
            if (node is not null)
                node.AssemblerScript = EditorText;
        }
        IsModified = false;
        StatusText = "Script saved.";
        RefreshScriptList();
    }

    [RelayCommand]
    private void DeleteScript()
    {
        if (_loadedNodeId is null) return;
        _addressTableService.RemoveEntry(_loadedNodeId);
        _loadedNodeId = null;
        EditorText = "";
        IsModified = false;
        StatusText = "Script deleted.";
        RefreshScriptList();
    }

    [RelayCommand]
    private void Validate()
    {
        if (_autoAssemblerEngine is null) { ValidationResult = "No assembler engine available."; return; }
        var result = _autoAssemblerEngine.Parse(EditorText);
        if (result.IsValid)
        {
            ValidationResult = "Valid.";
            if (result.Warnings.Count > 0)
                ValidationResult += " Warnings: " + string.Join("; ", result.Warnings);
        }
        else
        {
            ValidationResult = "Errors: " + string.Join("; ", result.Errors);
        }
    }

    [RelayCommand]
    private async Task EnableScriptAsync()
    {
        if (_autoAssemblerEngine is null || _loadedNodeId is null) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        try
        {
            var result = await _autoAssemblerEngine.EnableAsync(pid.Value, EditorText);
            StatusText = result.Success ? "Script enabled." : $"Enable failed: {result.Error}";
            var node = FindNode(_loadedNodeId);
            if (node is not null && result.Success) node.IsScriptEnabled = true;
            RefreshScriptList();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DisableScriptAsync()
    {
        if (_autoAssemblerEngine is null || _loadedNodeId is null) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        try
        {
            var result = await _autoAssemblerEngine.DisableAsync(pid.Value, EditorText);
            StatusText = result.Success ? "Script disabled." : $"Disable failed: {result.Error}";
            var node = FindNode(_loadedNodeId);
            if (node is not null && result.Success) node.IsScriptEnabled = false;
            RefreshScriptList();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private void InsertTemplate(string templateType)
    {
        var template = templateType switch
        {
            "aob_inject" => "[ENABLE]\naobscanmodule(INJECT,module.dll,48 8B 05 ?? ?? ?? ??)\nalloc(newmem,1024)\nlabel(code)\nlabel(return)\n\nnewmem:\ncode:\n  // Your code here\n  jmp return\n\nINJECT:\n  jmp newmem\n  nop 2\nreturn:\n\n[DISABLE]\nINJECT:\n  db 48 8B 05 ?? ?? ?? ??\ndealloc(newmem)\n",
            "code_cave" => "[ENABLE]\nalloc(cave,1024)\ncave:\n  // Your code here\n  ret\n\n[DISABLE]\ndealloc(cave)\n",
            "nop" => "[ENABLE]\n// address:\n//   db 90 90 90 90 90\n\n[DISABLE]\n// address:\n//   db ?? ?? ?? ?? ??\n",
            "jmp" => "[ENABLE]\n// address:\n//   jmp newAddress\n\n[DISABLE]\n// Restore original bytes\n",
            _ => ""
        };
        EditorText = template + EditorText;
        IsModified = true;
    }

    /// <summary>Open a specific script by node ID (called externally).</summary>
    public void OpenScript(string nodeId)
    {
        var node = FindNode(nodeId);
        if (node?.AssemblerScript is null) return;
        EditorText = node.AssemblerScript;
        _loadedNodeId = node.Id;
        IsModified = false;
        StatusText = $"Loaded: {node.Label}";
    }

    private AddressTableNode? FindNode(string id)
    {
        foreach (var root in _addressTableService.Roots)
        {
            var found = FindNodeRecursive(root, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static AddressTableNode? FindNodeRecursive(AddressTableNode node, string id)
    {
        if (node.Id == id) return node;
        foreach (var child in node.Children)
        {
            var found = FindNodeRecursive(child, id);
            if (found is not null) return found;
        }
        return null;
    }
}

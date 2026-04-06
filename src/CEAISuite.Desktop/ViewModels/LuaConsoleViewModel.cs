using System.Collections.ObjectModel;
using System.IO;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class LuaConsoleViewModel : ObservableObject
{
    private readonly ILuaScriptEngine? _luaEngine;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly IDialogService _dialogService;
    private readonly List<string> _commandHistory = new();
    private int _historyIndex = -1;

    public LuaConsoleViewModel(
        ILuaScriptEngine? luaEngine,
        IProcessContext processContext,
        IOutputLog outputLog,
        IDialogService dialogService)
    {
        _luaEngine = luaEngine;
        _processContext = processContext;
        _outputLog = outputLog;
        _dialogService = dialogService;
    }

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<LuaConsoleEntry> _history = new();

    [ObservableProperty]
    private bool _isExecuting;

    public bool IsEngineAvailable => _luaEngine is not null;

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText))
            return;

        if (_luaEngine is null)
        {
            AddEntry("error", "Lua engine is not available.");
            return;
        }

        var code = InputText;
        PushHistory(code);
        AddEntry("input", code);
        InputText = string.Empty;
        IsExecuting = true;

        try
        {
            var pid = _processContext.AttachedProcessId;
            var result = pid.HasValue
                ? await _luaEngine.ExecuteAsync(code, pid.Value)
                : await _luaEngine.ExecuteAsync(code);

            foreach (var line in result.OutputLines)
                AddEntry("output", line);

            if (result.ReturnValue is not null)
                AddEntry("result", result.ReturnValue);

            if (!result.Success)
                AddEntry("error", result.Error ?? "Unknown error");
        }
        catch (Exception ex)
        {
            AddEntry("error", ex.Message);
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private async Task EvaluateLineAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText))
            return;

        if (_luaEngine is null)
        {
            AddEntry("error", "Lua engine is not available.");
            return;
        }

        var expression = InputText;
        PushHistory(expression);
        AddEntry("input", $"= {expression}");
        InputText = string.Empty;
        IsExecuting = true;

        try
        {
            var result = await _luaEngine.EvaluateAsync(expression);

            if (result.Success && result.ReturnValue is not null)
                AddEntry("result", result.ReturnValue);
            else if (!result.Success)
                AddEntry("error", result.Error ?? "Unknown error");
            else
                AddEntry("result", "nil");
        }
        catch (Exception ex)
        {
            AddEntry("error", ex.Message);
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        History.Clear();
    }

    [RelayCommand]
    private void ResetEngine()
    {
        _luaEngine?.Reset();
        AddEntry("output", "Lua state reset.");
    }

    [RelayCommand]
    private void LoadFile()
    {
        var path = _dialogService.ShowOpenFileDialog("Lua Scripts (*.lua)|*.lua|All Files (*.*)|*.*");
        if (path is null) return;

        try
        {
            InputText = File.ReadAllText(path);
            AddEntry("output", $"Loaded: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AddEntry("error", $"Failed to load file: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SaveHistory()
    {
        var path = _dialogService.ShowSaveFileDialog("Text Files (*.txt)|*.txt|All Files (*.*)|*.*", "lua_console_history.txt");
        if (path is null) return;

        try
        {
            var lines = History.Select(e => $"[{e.Timestamp}] [{e.Type}] {e.Text}");
            File.WriteAllLines(path, lines);
            AddEntry("output", $"History saved to: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AddEntry("error", $"Failed to save history: {ex.Message}");
        }
    }

    [RelayCommand]
    private void HistoryUp()
    {
        if (_commandHistory.Count == 0) return;
        if (_historyIndex < 0) _historyIndex = _commandHistory.Count;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        InputText = _commandHistory[_historyIndex];
    }

    [RelayCommand]
    private void HistoryDown()
    {
        if (_commandHistory.Count == 0 || _historyIndex < 0) return;
        _historyIndex = Math.Min(_commandHistory.Count, _historyIndex + 1);
        InputText = _historyIndex < _commandHistory.Count ? _commandHistory[_historyIndex] : string.Empty;
    }

    private void PushHistory(string code)
    {
        if (_commandHistory.Count == 0 || _commandHistory[^1] != code)
            _commandHistory.Add(code);
        _historyIndex = -1; // Reset cursor
    }

    private void AddEntry(string type, string text)
    {
        History.Add(new LuaConsoleEntry(
            DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            type,
            text));
    }
}

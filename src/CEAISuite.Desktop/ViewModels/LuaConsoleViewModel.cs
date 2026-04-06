using System.Collections.ObjectModel;
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

    public LuaConsoleViewModel(
        ILuaScriptEngine? luaEngine,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _luaEngine = luaEngine;
        _processContext = processContext;
        _outputLog = outputLog;
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

    private void AddEntry(string type, string text)
    {
        History.Add(new LuaConsoleEntry(
            DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            type,
            text));
    }
}

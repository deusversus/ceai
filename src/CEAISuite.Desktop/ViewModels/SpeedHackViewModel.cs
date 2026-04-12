using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public sealed partial class SpeedHackViewModel : ObservableObject
{
    private readonly SpeedHackService _speedHackService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly IDispatcherService _dispatcher;

    [ObservableProperty] private double _multiplier = 1.0;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _statusText = "Not active";
    [ObservableProperty] private bool _patchTimeGetTime = true;
    [ObservableProperty] private bool _patchQueryPerformanceCounter = true;
    [ObservableProperty] private bool _patchGetTickCount64 = true;
    [ObservableProperty] private string _multiplierDisplay = "1.0x";

    public SpeedHackViewModel(
        SpeedHackService speedHackService,
        IProcessContext processContext,
        IOutputLog outputLog,
        IDispatcherService dispatcher)
    {
        _speedHackService = speedHackService;
        _processContext = processContext;
        _outputLog = outputLog;
        _dispatcher = dispatcher;
    }

    partial void OnMultiplierChanged(double value)
    {
        MultiplierDisplay = string.Create(CultureInfo.InvariantCulture, $"{value:F1}x");

        // Live update if active — debounce via dispatcher to avoid flooding
        if (IsActive && _processContext.AttachedProcessId is int pid)
        {
            _ = Task.Run(async () =>
            {
                var result = await _speedHackService.UpdateMultiplierAsync(pid, value).ConfigureAwait(false);
                if (!result.Success)
                    _dispatcher.Invoke(() => _outputLog.Append("SpeedHack", "Error", result.ErrorMessage ?? "Update failed"));
            });
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_processContext.AttachedProcessId is not int pid)
        {
            StatusText = "No process attached.";
            return;
        }

        var options = new SpeedHackOptions(PatchTimeGetTime, PatchQueryPerformanceCounter, PatchGetTickCount64);
        var result = await _speedHackService.ApplyAsync(pid, Multiplier, options).ConfigureAwait(false);

        _dispatcher.Invoke(() =>
        {
            if (result.Success)
            {
                IsActive = true;
                StatusText = $"Active at {Multiplier.ToString("F1", CultureInfo.InvariantCulture)}x — {string.Join(", ", result.PatchedFunctions ?? [])}";
                _outputLog.Append("SpeedHack", "Info", StatusText);
            }
            else
            {
                StatusText = result.ErrorMessage ?? "Failed";
                _outputLog.Append("SpeedHack", "Error", StatusText);
            }
        });
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_processContext.AttachedProcessId is not int pid)
        {
            StatusText = "No process attached.";
            return;
        }

        var result = await _speedHackService.RemoveAsync(pid).ConfigureAwait(false);

        _dispatcher.Invoke(() =>
        {
            if (result.Success)
            {
                IsActive = false;
                StatusText = "Removed. Original timing restored.";
                _outputLog.Append("SpeedHack", "Info", StatusText);
            }
            else
            {
                StatusText = result.ErrorMessage ?? "Failed";
                _outputLog.Append("SpeedHack", "Error", StatusText);
            }
        });
    }
}

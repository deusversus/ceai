namespace CEAISuite.Desktop.Services;

/// <summary>
/// Abstracts UI-thread dispatching so ViewModels can marshal calls
/// without directly depending on <see cref="System.Windows.Application"/>.
/// </summary>
public interface IDispatcherService
{
    void Invoke(Action action);
    Task InvokeAsync(Action action);
}

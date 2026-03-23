namespace CEAISuite.Desktop.Services;

public sealed class DispatcherService : IDispatcherService
{
    public void Invoke(Action action) =>
        System.Windows.Application.Current.Dispatcher.Invoke(action);

    public Task InvokeAsync(Action action) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task;
}

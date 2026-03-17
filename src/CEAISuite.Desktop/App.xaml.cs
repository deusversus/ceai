using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CEAISuite.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch unhandled UI-thread exceptions to prevent hard crashes
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Catch unhandled background-thread exceptions
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("DispatcherUnhandled", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe app will try to continue. Check logs for details.",
            "CE AI Suite — Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // Prevent crash
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogCrash("AppDomainUnhandled", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTask", e.Exception);
        e.SetObserved(); // Prevent crash
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CEAISuite", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"crash-{DateTime.Now:yyyy-MM-dd}.log");
            var entry = $"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(logPath, entry);
        }
        catch { /* last resort — nothing to do */ }
    }
}

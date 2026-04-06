using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Domain;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Engine.Windows;
using CEAISuite.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CEAISuite.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch unhandled UI-thread exceptions to prevent hard crashes
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Catch unhandled background-thread exceptions
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // ── Build DI container ──
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Wire the OutputLog sink now that DI is built
        var outputLog = Services.GetRequiredService<IOutputLog>();
        var outputSink = new OutputLogSerilogSink(outputLog);
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CEAISuite", "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDir, "ceaisuite-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.Sink(outputSink, Serilog.Events.LogEventLevel.Information)
            .CreateLogger();

        // Re-register the Serilog logger into the DI-provided ILoggerFactory
        var loggerFactory = Services.GetRequiredService<ILoggerFactory>();
        loggerFactory.AddSerilog(Log.Logger);
        ChatClientFactory.SetLogger(loggerFactory);

        // ── First-run welcome dialog ──
        var settingsService = Services.GetRequiredService<AppSettingsService>();
        if (settingsService.IsFirstRun)
        {
            var welcome = new WelcomeDialog();
            if (welcome.ShowDialog() == true)
            {
                if (!string.IsNullOrWhiteSpace(welcome.ApiKey))
                    settingsService.Settings.OpenAiApiKey = welcome.ApiKey;
                settingsService.Settings.Theme = welcome.SelectedTheme;
                settingsService.Settings.DensityPreset = welcome.SelectedDensity;
            }
            // Mark first run complete regardless of OK/cancel
            settingsService.Settings.FirstRunCompleted = true;
            settingsService.Save();
        }

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite",
            "workspace.db");

        // ── Engine implementations (singletons) ──
        services.AddSingleton<IEngineFacade, WindowsEngineFacade>();
        services.AddSingleton<IScanEngine, WindowsScanEngine>();
        services.AddSingleton<ISymbolEngine, WindowsSymbolEngine>();
        services.AddSingleton<IDisassemblyEngine>(sp =>
            new WindowsDisassemblyEngine(sp.GetRequiredService<ISymbolEngine>()));
        services.AddSingleton<IBreakpointEngine, WindowsBreakpointEngine>();
        services.AddSingleton<ILuaFormHost, LuaFormHostService>();
        services.AddSingleton<ILuaScriptEngine>(sp =>
            new MoonSharpLuaEngine(
                sp.GetRequiredService<IEngineFacade>(),
                sp.GetService<IAutoAssemblerEngine>(),
                formHost: sp.GetService<ILuaFormHost>()));
        services.AddSingleton<IAutoAssemblerEngine>(sp =>
            new WindowsAutoAssemblerEngine(() => sp.GetService<ILuaScriptEngine>()));
        services.AddSingleton<IMemoryProtectionEngine, WindowsMemoryProtectionEngine>();
        services.AddSingleton<ICodeCaveEngine, WindowsCodeCaveEngine>();
        services.AddSingleton<IScreenCaptureEngine, WindowsScreenCaptureEngine>();
        services.AddSingleton<ICallStackEngine, WindowsCallStackEngine>();

        // ── Persistence ──
        services.AddSingleton<IInvestigationSessionRepository>(
            _ => new SqliteInvestigationSessionRepository(databasePath));

        // ── Application services ──
        services.AddSingleton<WorkspaceDashboardService>();
        services.AddSingleton<ScanService>();
        services.AddSingleton<AddressTableService>();
        services.AddSingleton<DisassemblyService>();
        services.AddSingleton<ScriptGenerationService>();
        services.AddSingleton<AddressTableExportService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<BreakpointService>(sp =>
            new BreakpointService(
                sp.GetRequiredService<IBreakpointEngine>(),
                sp.GetService<ILuaScriptEngine>()));
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<PatchUndoService>();
        services.AddSingleton<MemorySnapshotService>();
        services.AddSingleton<PointerRescanService>();
        services.AddSingleton<SignatureGeneratorService>();
        services.AddSingleton<ProcessWatchdogService>();
        services.AddSingleton<OperationJournal>();
        services.AddSingleton<AiChatStore>();

        // ── Settings (needs .Load() called) ──
        services.AddSingleton<AppSettingsService>(sp =>
        {
            var svc = new AppSettingsService(sp.GetService<ILogger<AppSettingsService>>());
            svc.Load();
            return svc;
        });

        // ── Token limits (resolved from settings) ──
        services.AddSingleton<TokenLimits>(sp =>
            TokenLimits.Resolve(sp.GetRequiredService<AppSettingsService>().Settings));

        services.AddSingleton<ToolResultStore>();

        // ── AI tool functions ──
        // The currentChatProvider delegate late-binds to AiOperatorService.DisplayHistory
        // because AiToolFunctions is constructed before AiOperatorService is resolved.
        services.AddSingleton<AiToolFunctions>(sp =>
        {
            // Use Lazy to break the circular dependency: AiToolFunctions needs
            // AiOperatorService.DisplayHistory, but AiOperatorService needs AiToolFunctions.
            var lazyOperator = new Lazy<AiOperatorService>(() => sp.GetRequiredService<AiOperatorService>());
            return new AiToolFunctions(
                sp.GetRequiredService<IEngineFacade>(),
                sp.GetRequiredService<WorkspaceDashboardService>(),
                sp.GetRequiredService<ScanService>(),
                sp.GetRequiredService<AddressTableService>(),
                sp.GetRequiredService<DisassemblyService>(),
                sp.GetRequiredService<ScriptGenerationService>(),
                sp.GetRequiredService<BreakpointService>(),
                sp.GetRequiredService<IAutoAssemblerEngine>(),
                sp.GetRequiredService<IScreenCaptureEngine>(),
                sp.GetRequiredService<GlobalHotkeyService>(),
                sp.GetRequiredService<PatchUndoService>(),
                sp.GetRequiredService<SessionService>(),
                sp.GetRequiredService<SignatureGeneratorService>(),
                sp.GetRequiredService<IMemoryProtectionEngine>(),
                sp.GetRequiredService<MemorySnapshotService>(),
                sp.GetRequiredService<PointerRescanService>(),
                sp.GetRequiredService<ICallStackEngine>(),
                sp.GetRequiredService<ICodeCaveEngine>(),
                sp.GetRequiredService<ProcessWatchdogService>(),
                sp.GetRequiredService<OperationJournal>(),
                sp.GetRequiredService<AiChatStore>(),
                currentChatProvider: () => lazyOperator.Value.DisplayHistory ?? Array.Empty<AiChatMessage>(),
                tokenLimits: sp.GetRequiredService<TokenLimits>(),
                toolResultStore: sp.GetRequiredService<ToolResultStore>(),
                luaEngine: sp.GetService<ILuaScriptEngine>());
        });

        // ── AI operator service (starts with null IChatClient — MainWindow hot-swaps it) ──
        // The contextProvider delegate is wired by MainWindow after construction via SetContextProvider().
        services.AddSingleton<AiOperatorService>(sp =>
            new AiOperatorService(
                chatClient: null,
                sp.GetRequiredService<AiToolFunctions>(),
                contextProvider: null,
                sp.GetRequiredService<AiChatStore>(),
                sp.GetRequiredService<AppSettingsService>()));

        // ── Desktop shared services ──
        services.AddSingleton<IProcessContext, ProcessContext>();
        services.AddSingleton<IOutputLog, OutputLogService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IDispatcherService, DispatcherService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAiContextService, AiContextService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());

        // ── Bottom-panel ViewModels ──
        services.AddSingleton<OutputLogViewModel>();
        services.AddSingleton<HotkeysViewModel>();
        services.AddSingleton<FindResultsViewModel>();
        services.AddSingleton<SnapshotsViewModel>();
        services.AddSingleton<JournalViewModel>();
        services.AddSingleton<LuaConsoleViewModel>(sp =>
            new LuaConsoleViewModel(
                sp.GetService<ILuaScriptEngine>(),
                sp.GetRequiredService<IProcessContext>(),
                sp.GetRequiredService<IOutputLog>(),
                sp.GetRequiredService<IDialogService>()));
        services.AddSingleton<BreakpointsViewModel>();
        services.AddSingleton<ScriptsViewModel>();

        // ── Phase 2.5 panel ViewModels ──
        services.AddSingleton<ScannerViewModel>();
        services.AddSingleton<ProcessListViewModel>();
        services.AddSingleton<InspectionViewModel>();
        services.AddSingleton<AiOperatorViewModel>();
        services.AddSingleton<AddressTableViewModel>(sp =>
            new AddressTableViewModel(
                sp.GetRequiredService<AddressTableService>(),
                sp.GetRequiredService<AddressTableExportService>(),
                sp.GetRequiredService<IProcessContext>(),
                sp.GetService<IAutoAssemblerEngine>(),
                sp.GetRequiredService<BreakpointService>(),
                sp.GetRequiredService<DisassemblyService>(),
                sp.GetRequiredService<ScriptGenerationService>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<IOutputLog>(),
                sp.GetRequiredService<IDispatcherService>(),
                sp.GetRequiredService<INavigationService>(),
                sp.GetService<ILuaScriptEngine>()));

        // ── Phase 3 services (not yet in DI) ──
        services.AddSingleton<StructureDissectorService>();
        services.AddSingleton<PointerScannerService>();

        // ── Phase 3 center-tab ViewModels ──
        services.AddSingleton<StructureDissectorViewModel>();
        services.AddSingleton<PointerScannerViewModel>();
        services.AddSingleton<DisassemblerViewModel>();
        services.AddSingleton<ScriptEditorViewModel>(sp =>
            new ScriptEditorViewModel(
                sp.GetRequiredService<AddressTableService>(),
                sp.GetService<IAutoAssemblerEngine>(),
                sp.GetRequiredService<ScriptGenerationService>(),
                sp.GetRequiredService<IProcessContext>(),
                sp.GetRequiredService<IOutputLog>()));
        services.AddSingleton<DebuggerViewModel>();

        // ── Phase 5 Memory Browser ──
        services.AddSingleton<CodeInjectionTemplateService>();
        services.AddSingleton<MemoryBrowserViewModel>();

        // ── Phase 4 Explorer ViewModels ──
        services.AddSingleton<ModuleListViewModel>();
        services.AddSingleton<ThreadListViewModel>();
        services.AddSingleton<MemoryRegionsViewModel>();
        services.AddSingleton<WorkspaceViewModel>();

        // ── Logging ──
        services.AddLogging();

        // ── Main ViewModel + Window ──
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        (Services as IDisposable)?.Dispose();
        base.OnExit(e);
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
            var entry = $"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n";
            // Include full inner exception chain
            var inner = ex.InnerException;
            while (inner is not null)
            {
                entry += $"  ---> {inner.GetType().Name}: {inner.Message}\n  {inner.StackTrace}\n";
                inner = inner.InnerException;
            }
            entry += "\n";
            File.AppendAllText(logPath, entry);
        }
        catch (Exception writeEx) { System.Diagnostics.Trace.TraceWarning($"[App] Failed to write crash log: {writeEx.Message}"); }
    }
}

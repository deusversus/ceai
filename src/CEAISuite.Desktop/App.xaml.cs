using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
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

        // Initialize Gemini OAuth service from encrypted settings (if configured)
        var settingsService = Services.GetRequiredService<AppSettingsService>();
        if (!string.IsNullOrWhiteSpace(settingsService.Settings.GeminiOAuthClientId)
            && !string.IsNullOrWhiteSpace(settingsService.Settings.GeminiOAuthClientSecret))
        {
            ChatClientFactory.SetGeminiOAuth(
                settingsService.Settings.GeminiOAuthClientId,
                settingsService.Settings.GeminiOAuthClientSecret);
        }

        // ── First-run welcome dialog ──
        if (settingsService.IsFirstRun)
        {
            // Prevent app shutdown when the dialog is closed — MainWindow hasn't been
            // created yet, so WPF's default OnLastWindowClose would terminate the app.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var welcome = new WelcomeDialog();
            if (welcome.ShowDialog() == true)
            {
                settingsService.Settings.Provider = welcome.SelectedProvider;
                if (!string.IsNullOrWhiteSpace(welcome.ApiKey))
                {
                    switch (welcome.SelectedProvider)
                    {
                        case "openai": settingsService.Settings.OpenAiApiKey = welcome.ApiKey; break;
                        case "anthropic": settingsService.Settings.AnthropicApiKey = welcome.ApiKey; break;
                        case "gemini": settingsService.Settings.GeminiApiKey = welcome.ApiKey; break;
                        case "openrouter": settingsService.Settings.OpenRouterApiKey = welcome.ApiKey; break;
                        case "openai-compatible": settingsService.Settings.CompatibleApiKey = welcome.ApiKey; break;
                    }
                }
                settingsService.Settings.Theme = welcome.SelectedTheme;
                settingsService.Settings.DensityPreset = welcome.SelectedDensity;
            }
            settingsService.Settings.FirstRunCompleted = true;
            settingsService.Save();

            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }

        // ── Crash recovery: check for recovery file before showing main window ──
        var recoveryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite", "recovery.json");
        if (File.Exists(recoveryPath))
        {
            var result = MessageBox.Show(
                "CE AI Suite didn't shut down cleanly. Restore your last session?",
                "Session Recovery",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    var exportService = Services.GetRequiredService<AddressTableExportService>();
                    var tableService = Services.GetRequiredService<AddressTableService>();
                    var recovered = exportService.ImportRecoveryAsync(recoveryPath).GetAwaiter().GetResult();
                    if (recovered is not null)
                        tableService.ImportNodes(recovered);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to restore recovery data");
                }
            }

            try { File.Delete(recoveryPath); } catch { /* best-effort */ }
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
        services.AddSingleton<UpdateService>();
        services.AddSingleton<PluginHost>(sp => new PluginHost());
        services.AddSingleton<UiCommandBus>();
        services.AddSingleton<IUiCommandBus>(sp => sp.GetRequiredService<UiCommandBus>());


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
                sp.GetService<PointerScannerService>(),
                sp.GetRequiredService<PointerRescanService>(),
                sp.GetRequiredService<ICallStackEngine>(),
                sp.GetRequiredService<ICodeCaveEngine>(),
                sp.GetRequiredService<ProcessWatchdogService>(),
                sp.GetRequiredService<OperationJournal>(),
                sp.GetRequiredService<AiChatStore>(),
                currentChatProvider: () => lazyOperator.Value.DisplayHistory ?? Array.Empty<AiChatMessage>(),
                tokenLimits: sp.GetRequiredService<TokenLimits>(),
                toolResultStore: sp.GetRequiredService<ToolResultStore>(),
                luaEngine: sp.GetService<ILuaScriptEngine>(),
                pluginHost: sp.GetRequiredService<PluginHost>(),
                uiCommandBus: sp.GetRequiredService<IUiCommandBus>());
        });

        // ── AI operator service (starts with null IChatClient — MainWindow hot-swaps it) ──
        // The contextProvider delegate is wired by MainWindow after construction via SetContextProvider().
        services.AddSingleton<AiOperatorService>(sp =>
            new AiOperatorService(
                chatClient: null,
                sp.GetRequiredService<AiToolFunctions>(),
                contextProvider: null,
                sp.GetRequiredService<AiChatStore>(),
                sp.GetRequiredService<AppSettingsService>(),
                sp.GetRequiredService<PluginHost>()));

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

        // ── Phase 3 services ──
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
        services.AddSingleton<PluginManagerViewModel>();

        // ── Logging ──
        services.AddLogging();

        // ── Main ViewModel + Window ──
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        if (Services is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            (Services as IDisposable)?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        EmergencySaveAddressTable();
        LogCrash("DispatcherUnhandled", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe app will try to continue. Check logs for details.",
            "CE AI Suite — Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // Prevent crash
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        EmergencySaveAddressTable();
        if (e.ExceptionObject is Exception ex)
            LogCrash("AppDomainUnhandled", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTask", e.Exception);
        e.SetObserved(); // Prevent crash
    }

    private static void EmergencySaveAddressTable()
    {
        try
        {
            var exportService = Services?.GetService<AddressTableExportService>();
            var tableService = Services?.GetService<AddressTableService>();
            if (exportService is null || tableService is null) return;

            var roots = tableService.Roots;
            if (roots.Count == 0) return;

            var recoveryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CEAISuite", "recovery.json");
            exportService.ExportRecoveryAsync(roots, recoveryPath).GetAwaiter().GetResult();
        }
        catch { /* Best-effort -- don't throw during crash handler */ }
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

            // Phase 9C: Write submission-ready telemetry JSON if user has opted in
            WriteCrashTelemetry(logDir, source, ex);
        }
        catch (Exception writeEx) { System.Diagnostics.Trace.TraceWarning($"[App] Failed to write crash log: {writeEx.Message}"); }
    }

    /// <summary>
    /// Writes a structured JSON crash report alongside the plain-text log.
    /// Only writes if the user has opted in via <see cref="AppSettings.EnableCrashTelemetry"/>.
    /// The JSON file is ready for future submission to a telemetry endpoint.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions s_telemetryJsonOptions = new() { WriteIndented = true };

    private static void WriteCrashTelemetry(string logDir, string source, Exception ex)
    {
        try
        {
            var settingsService = Services?.GetService<AppSettingsService>();
            if (settingsService is null || !settingsService.Settings.EnableCrashTelemetry) return;

            var telemetry = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                source,
                exceptionType = ex.GetType().FullName,
                message = ex.Message,
                stackTrace = ex.StackTrace,
                innerException = ex.InnerException is { } inner ? new
                {
                    type = inner.GetType().FullName,
                    message = inner.Message,
                    stackTrace = inner.StackTrace
                } : null,
                appVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                os = Environment.OSVersion.ToString(),
                runtime = Environment.Version.ToString()
            };

            var json = System.Text.Json.JsonSerializer.Serialize(telemetry, s_telemetryJsonOptions);
            var telemetryPath = Path.Combine(logDir, $"crash-telemetry-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(telemetryPath, json);
        }
        catch { /* Best-effort — don't throw inside crash handler */ }
    }
}

using Serilog.Core;
using Serilog.Events;

namespace CEAISuite.Desktop.Services;

/// <summary>
/// Serilog sink that routes log events to the existing <see cref="IOutputLog"/> panel,
/// bridging structured logging with the in-app Output tab.
/// </summary>
public sealed class OutputLogSerilogSink(IOutputLog outputLog) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => "TRACE",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARN",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "FATAL",
            _ => "INFO"
        };

        var source = logEvent.Properties.TryGetValue("SourceContext", out var ctx)
            ? ctx.ToString().Trim('"').Split('.')[^1]
            : "App";

        outputLog.Append(source, level, logEvent.RenderMessage());
    }
}

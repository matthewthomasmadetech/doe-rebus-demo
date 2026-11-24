using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace RebusDemo.Logging;

/// <summary>Sets the same console logging defaults for all four services.</summary>
internal static class DemoConsoleLogging
{
    /// <summary>Highlights workflow logs and keeps infrastructure warnings and errors visible.</summary>
    public static void AddDemoConsole(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        // The dot keeps the prefix filter from also matching the RebusDemo application categories.
        logging.AddFilter("Rebus.", LogLevel.Warning);
        logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        logging.AddConsole();
        logging.AddConsoleFormatter<WorkflowConsoleFormatter, ConsoleFormatterOptions>();

        // Use the demo format by default while allowing the built-in JSON formatter via configuration.
        logging.Services.PostConfigure<ConsoleLoggerOptions>(options =>
            options.FormatterName ??= WorkflowConsoleFormatter.FormatterName);
    }
}

/// <summary>Gives the demo's message handlers, saga and endpoint a distinct console prefix.</summary>
internal sealed class WorkflowConsoleFormatter(IHostEnvironment environment)
    : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "workflow";

    /// <summary>Renders one log entry, retaining diagnostic categories and exception details.</summary>
    public override void Write<TState>(
        in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
            return;

        // The demo's workflow log categories come from ILogger<T> in the RebusDemo namespaces.
        if (logEntry.Category.StartsWith("RebusDemo.", StringComparison.Ordinal))
        {
            textWriter.WriteLine();
            textWriter.Write($">>> [{environment.ApplicationName}] ");
            if (logEntry.LogLevel >= LogLevel.Warning)
                textWriter.Write($"[{logEntry.LogLevel}] ");
        }
        else
        {
            textWriter.Write($"[{logEntry.LogLevel}] {logEntry.Category} | ");
        }

        textWriter.WriteLine(message);
        if (logEntry.Exception is not null)
            textWriter.WriteLine(logEntry.Exception);
    }
}

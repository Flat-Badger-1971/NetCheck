using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace NetCheck.Logging;

public sealed class MinimalConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "minimal";

    // Basic ANSI colour codes (avoid complicated 256-colour for portability)
    private const string Reset = "\u001b[0m";
    private const string Dim = "\u001b[2m";
    private const string Gray = "\u001b[90m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Red = "\u001b[31m";
    private const string BrightRed = "\u001b[91m";
    private const string Cyan = "\u001b[36m";

    private readonly bool _enableColour;

    public MinimalConsoleFormatter() : base(FormatterName)
    {
        // Respect NO_COLOR environment convention to disable colors
        _enableColour = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider scopeProvider,
        TextWriter textWriter)
    {
        string message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        // Build final message (append exception summary inline)
        if (logEntry.Exception != null)
        {
            message = $"{message} Exception: {logEntry.Exception.GetType().Name}: {logEntry.Exception.Message}";
        }

        string levelToken = GetLevelToken(logEntry.LogLevel);
        string colouredPrefix = _enableColour ? Colourise(logEntry.LogLevel, levelToken) : levelToken;

        // Single-line output: "<level>: <message>"
        textWriter.Write(colouredPrefix);
        textWriter.Write(": ");
        textWriter.WriteLine(message);
    }

    private static string GetLevelToken(LogLevel level) =>
        level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "crit",
            _ => level.ToString().ToLowerInvariant()
        };

    private static string GetAnsiColour(LogLevel level) =>
        level switch
        {
            LogLevel.Trace => Dim + Gray,
            LogLevel.Debug => Gray,
            LogLevel.Information => Green,
            LogLevel.Warning => Yellow,
            LogLevel.Error => Red,
            LogLevel.Critical => BrightRed,
            _ => Cyan
        };

    private static string Colourise(LogLevel level, string text)
    {
        string colour = GetAnsiColour(level);
        return $"{colour}{text}{Reset}";
    }
}

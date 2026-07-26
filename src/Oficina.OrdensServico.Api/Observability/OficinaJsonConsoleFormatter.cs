using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Oficina.OrdensServico.Api.Observability;

internal sealed class OficinaJsonConsoleFormatterOptions : ConsoleFormatterOptions
{
    public string ServiceName { get; set; } = string.Empty;
    public string? ServiceVersion { get; set; }
    public string? DeploymentEnvironment { get; set; }
}

/// <summary>
/// Formatter JSON com os campos no nivel superior do registro.
/// AddJsonConsole com IncludeScopes emitiria trace.id e span.id dentro de um
/// array Scopes, e o New Relic exige esses campos no topo para correlacionar
/// logs com traces.
/// </summary>
internal sealed class OficinaJsonConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "oficina-json";

    // Somente estas chaves de scope e de state sao serializadas. Qualquer outro
    // valor estruturado fica de fora por construcao, e nao por filtro.
    private static readonly Dictionary<string, string> AllowedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CorrelationId"] = "correlationId",
            ["OrdemServicoId"] = "ordemServicoId",
            ["MessageId"] = "messageId",
            ["MessageType"] = "messageType",
            ["SagaState"] = "sagaState"
        };

    private readonly IOptionsMonitor<OficinaJsonConsoleFormatterOptions> _options;

    public OficinaJsonConsoleFormatter(IOptionsMonitor<OficinaJsonConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        _options = options;
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var options = _options.CurrentValue;
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectFromState(logEntry.State, attributes);
        CollectFromScopes(scopeProvider, attributes);

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            writer.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("level", LevelName(logEntry.LogLevel));
            writer.WriteString("message", LogSanitizer.Sanitize(message) ?? string.Empty);
            writer.WriteString("category", logEntry.Category);

            writer.WriteString("service.name", options.ServiceName);
            WriteIfPresent(writer, "service.version", options.ServiceVersion);
            WriteIfPresent(writer, "deployment.environment", options.DeploymentEnvironment);

            // Sem Activity corrente os campos de trace sao omitidos: emitir
            // string vazia criaria correlacao inexistente no New Relic.
            var activity = Activity.Current;
            if (activity is not null)
            {
                writer.WriteString("trace.id", activity.TraceId.ToHexString());
                writer.WriteString("span.id", activity.SpanId.ToHexString());
            }

            foreach (var name in AllowedKeys.Values)
            {
                if (attributes.TryGetValue(name, out var value))
                {
                    WriteIfPresent(writer, name, value);
                }
            }

            if (logEntry.Exception is not null)
            {
                writer.WriteString("exception.type", logEntry.Exception.GetType().FullName);
                WriteIfPresent(writer, "exception.message", LogSanitizer.Sanitize(logEntry.Exception.Message));
                // Exception.Data nunca e serializada: e um dicionario livre onde
                // qualquer chamador pode ter colocado dado sensivel.
                WriteIfPresent(
                    writer,
                    "exception.stacktrace",
                    LogSanitizer.Sanitize(logEntry.Exception.StackTrace, LogSanitizer.MaxStackTraceLength));
            }

            writer.WriteEndObject();
        }

        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void CollectFromScopes(IExternalScopeProvider? scopeProvider, Dictionary<string, string> target)
    {
        scopeProvider?.ForEachScope(
            static (scope, state) => CollectFromState(scope, state),
            target);
    }

    private static void CollectFromState(object? state, Dictionary<string, string> target)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                if (AllowedKeys.TryGetValue(pair.Key, out var name) && pair.Value is not null)
                {
                    var value = pair.Value.ToString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        target[name] = LogSanitizer.Truncate(value, 256)!;
                    }
                }
            }
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "NONE"
    };
}

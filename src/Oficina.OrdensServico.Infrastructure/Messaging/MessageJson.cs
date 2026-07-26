using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

internal static class MessageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string TraceparentProperty = "traceparent";
    private const string TracestateProperty = "tracestate";
    private static readonly TextMapPropagator TraceContext = new TraceContextPropagator();

    /// <summary>
    /// O Outbox e gravado dentro da transacao de negocio e publicado depois, em
    /// outro contexto. Para o trace ligar da origem ate o consumidor, o contexto
    /// precisa ser capturado aqui, no momento da criacao. Adicionar coluna
    /// exigiria migration, entao o contexto viaja no proprio envelope, que ja e
    /// persistido em OutboxMessage.Body.
    /// </summary>
    public static string Envelope(string messageType, Guid ordemServicoId, string correlationId, string? causationId, object payload)
    {
        var carrier = new Dictionary<string, string>(StringComparer.Ordinal);
        var activity = Activity.Current;
        if (activity is not null)
        {
            TraceContext.Inject(
                new PropagationContext(activity.Context, Baggage.Current),
                carrier,
                static (target, key, value) => target[key] = value);
        }

        var envelope = new
        {
            messageId = Guid.NewGuid(),
            messageType,
            schemaVersion = 1,
            occurredAtUtc = DateTimeOffset.UtcNow,
            correlationId,
            causationId,
            ordemServicoId,
            payload,
            traceparent = carrier.GetValueOrDefault(TraceparentProperty),
            tracestate = carrier.GetValueOrDefault(TracestateProperty)
        };
        return JsonSerializer.Serialize(envelope, Options);
    }

    /// <summary>
    /// Reescreve somente traceparent e tracestate.
    /// A instrumentacao AWS injeta o contexto do span de SendMessage nos
    /// MessageAttributes, mas nao atualiza o JSON do envelope. Sem esta
    /// transferencia no receiver, o Consumer viraria filho do contexto anterior
    /// ao envio: mesmo traceId, relacao causal errada.
    /// O contrato e preservar semanticamente o payload e as propriedades
    /// desconhecidas; nao se promete igualdade byte a byte, porque reserializar
    /// pode alterar espacos, escaping e formatacao numerica.
    /// </summary>
    public static string WithTraceContext(string body, string? traceparent, string? tracestate)
    {
        if (string.IsNullOrWhiteSpace(traceparent))
        {
            return body;
        }

        var node = JsonNode.Parse(body);
        if (node is not JsonObject json)
        {
            return body;
        }

        json[TraceparentProperty] = traceparent;
        json[TracestateProperty] = string.IsNullOrWhiteSpace(tracestate) ? null : tracestate;
        return json.ToJsonString(Options);
    }

    public static MessageEnvelope ParseAndValidate(string body)
    {
        var envelope = JsonSerializer.Deserialize<MessageEnvelope>(body, Options)
            ?? throw new InvalidOperationException("Envelope ausente.");
        if (envelope.MessageId == Guid.Empty) throw new InvalidOperationException("MessageId invalido.");
        if (string.IsNullOrWhiteSpace(envelope.MessageType)) throw new InvalidOperationException("MessageType invalido.");
        if (envelope.SchemaVersion != 1) throw new InvalidOperationException("SchemaVersion invalida.");
        if (envelope.OrdemServicoId == Guid.Empty) throw new InvalidOperationException("OrdemServicoId invalido.");
        if (string.IsNullOrWhiteSpace(envelope.CorrelationId)) throw new InvalidOperationException("CorrelationId invalido.");
        return envelope;
    }
}

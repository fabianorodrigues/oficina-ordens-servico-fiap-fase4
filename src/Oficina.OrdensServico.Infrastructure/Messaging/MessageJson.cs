using System.Text.Json;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

internal static class MessageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Envelope(string messageType, Guid ordemServicoId, string correlationId, string? causationId, object payload)
    {
        var envelope = new
        {
            messageId = Guid.NewGuid(),
            messageType,
            schemaVersion = 1,
            occurredAtUtc = DateTimeOffset.UtcNow,
            correlationId,
            causationId,
            ordemServicoId,
            payload
        };
        return JsonSerializer.Serialize(envelope, Options);
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

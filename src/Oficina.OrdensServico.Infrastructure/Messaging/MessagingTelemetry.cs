using System.Diagnostics;
using Amazon.SQS.Model;
using Oficina.OrdensServico.Infrastructure.Observability;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

/// <summary>
/// Uma unica fonte de span por etapa do fluxo SQS.
/// A instrumentacao AWS ja cria o span de envio e injeta a propagacao nos
/// MessageAttributes, portanto aqui nao existe Activity de Producer nem
/// injecao manual de traceparent: somar os dois mecanismos produziria spans
/// duplicados e dashboards contando a mesma publicacao duas vezes.
/// </summary>
internal static class MessagingTelemetry
{
    private const string MessagingSystem = "aws.sqs";
    private static readonly TextMapPropagator TraceContext = new TraceContextPropagator();

    /// <summary>
    /// Span do despacho do Outbox. Kind Internal, e nao Producer: quem publica e
    /// a instrumentacao AWS, dentro deste escopo. O parent vem do envelope, que
    /// e o que liga a publicacao a requisicao de origem.
    /// </summary>
    public static Activity? StartOutboxDispatch(OutboxMessage message, string destination)
    {
        var parent = ParseParent(message.Body);
        var activity = OficinaTelemetry.ActivitySource.StartActivity(
            OficinaTelemetry.OutboxDispatchActivity,
            ActivityKind.Internal,
            parentContext: parent);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag(OficinaTelemetry.Attributes.MessagingSystem, MessagingSystem);
        activity.SetTag(OficinaTelemetry.Attributes.DestinationName, destination);
        activity.SetTag(OficinaTelemetry.Attributes.MessageId, message.MessageId.ToString());
        activity.SetTag(OficinaTelemetry.Attributes.MessageType, message.MessageType);
        activity.SetTag(OficinaTelemetry.Attributes.OrdemId, message.OrdemServicoId.ToString());
        activity.SetTag(OficinaTelemetry.Attributes.CorrelationId, message.CorrelationId);
        return activity;
    }

    /// <summary>
    /// Unica Activity de consumo do fluxo. O receiver nao cria span: ele apenas
    /// transfere o contexto dos MessageAttributes para o envelope persistido.
    /// </summary>
    public static Activity? StartInboxConsume(InboxMessage inbox, string destination)
    {
        var parent = ParseParent(inbox.Body);
        var activity = OficinaTelemetry.ActivitySource.StartActivity(
            OficinaTelemetry.InboxConsumeActivity,
            ActivityKind.Consumer,
            parentContext: parent);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag(OficinaTelemetry.Attributes.MessagingSystem, MessagingSystem);
        activity.SetTag(OficinaTelemetry.Attributes.DestinationName, destination);
        activity.SetTag(OficinaTelemetry.Attributes.MessageId, inbox.MessageId.ToString());
        activity.SetTag(OficinaTelemetry.Attributes.MessageType, inbox.MessageType);
        activity.SetTag(OficinaTelemetry.Attributes.OrdemId, inbox.OrdemServicoId.ToString());
        activity.SetTag(OficinaTelemetry.Attributes.CorrelationId, inbox.CorrelationId);
        return activity;
    }

    /// <summary>
    /// Atributos de negocio da mensagem. traceparent e tracestate nao entram
    /// aqui: sao responsabilidade da instrumentacao AWS.
    /// </summary>
    public static Dictionary<string, MessageAttributeValue> BusinessAttributes(OutboxMessage message)
    {
        var attributes = new Dictionary<string, MessageAttributeValue>(StringComparer.Ordinal)
        {
            [OficinaTelemetry.MessageAttributeNames.CorrelationId] = String(message.CorrelationId),
            [OficinaTelemetry.MessageAttributeNames.OrdemServicoId] = String(message.OrdemServicoId.ToString()),
            [OficinaTelemetry.MessageAttributeNames.MessageType] = String(message.MessageType)
        };

        if (!string.IsNullOrWhiteSpace(message.CausationId))
        {
            attributes[OficinaTelemetry.MessageAttributeNames.CausationId] = String(message.CausationId);
        }

        return attributes;
    }

    public static (string? Traceparent, string? Tracestate) ReadTraceContext(Message message)
    {
        if (message.MessageAttributes is null || message.MessageAttributes.Count == 0)
        {
            return (null, null);
        }

        return (
            message.MessageAttributes.GetValueOrDefault(OficinaTelemetry.MessageAttributeNames.Traceparent)?.StringValue,
            message.MessageAttributes.GetValueOrDefault(OficinaTelemetry.MessageAttributeNames.Tracestate)?.StringValue);
    }

    public static void SetResult(Activity? activity, string result)
        => activity?.SetTag(OficinaTelemetry.Attributes.ProcessingResult, result);

    public static void SetFailure(Activity? activity, Exception exception)
    {
        activity?.SetTag(OficinaTelemetry.Attributes.ProcessingResult, "failed");
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
    }

    private static ActivityContext ParseParent(string body)
    {
        string? traceparent = null;
        string? tracestate = null;
        try
        {
            var envelope = MessageJson.ParseAndValidate(body);
            traceparent = envelope.Traceparent;
            tracestate = envelope.Tracestate;
        }
        catch (Exception)
        {
            // Corpo invalido e tratado pelo fluxo de negocio. Telemetria nunca
            // pode transformar isso numa segunda falha.
            return default;
        }

        if (string.IsNullOrWhiteSpace(traceparent))
        {
            return default;
        }

        var carrier = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["traceparent"] = traceparent
        };
        if (!string.IsNullOrWhiteSpace(tracestate))
        {
            carrier["tracestate"] = tracestate;
        }

        var context = TraceContext.Extract(
            default,
            carrier,
            static (source, key) => source.TryGetValue(key, out var value) ? [value] : []);

        return context.ActivityContext;
    }

    private static MessageAttributeValue String(string value)
        => new() { DataType = "String", StringValue = value };
}

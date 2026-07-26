using System.Diagnostics;
using System.Diagnostics.Metrics;
using Oficina.OrdensServico.Application.Abstractions;

namespace Oficina.OrdensServico.Infrastructure.Observability;

/// <summary>
/// Fonte unica de spans manuais e de metricas de negocio.
/// O span de envio ao SQS e criado pela instrumentacao AWS, que tambem injeta a
/// propagacao nos MessageAttributes. Aqui ficam apenas os spans que a
/// instrumentacao nao cobre: despacho do Outbox, consumo do Inbox e o
/// processamento de pagamento.
/// </summary>
public static class OficinaTelemetry
{
    public const string ActivitySourceName = "Oficina.OrdensServico";
    public const string MeterName = "Oficina.OrdensServico";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public const string OutboxDispatchActivity = "oficina.outbox.dispatch";
    public const string InboxConsumeActivity = "oficina.inbox.consume";
    public const string PagamentoActivity = "oficina.pagamento.processar";

    public static class Attributes
    {
        public const string CorrelationId = "correlationId";
        public const string CausationId = "causationId";
        public const string OrdemId = "oficina.ordem.id";
        public const string SagaPreviousState = "oficina.saga.previous_state";
        public const string SagaCurrentState = "oficina.saga.current_state";
        public const string ProcessingResult = "oficina.processing.result";
        public const string MessagingSystem = "messaging.system";
        public const string MessageId = "messaging.message.id";
        public const string MessageType = "messaging.message.type";
        public const string DestinationName = "messaging.destination.name";
    }

    public static class MessageAttributeNames
    {
        public const string Traceparent = "traceparent";
        public const string Tracestate = "tracestate";
        public const string CorrelationId = "correlationId";
        public const string CausationId = "causationId";
        public const string OrdemServicoId = "ordemServicoId";
        public const string MessageType = "messageType";
    }

    /// <summary>
    /// Conjuntos fechados das dimensoes. Texto livre em dimensao de metrica
    /// explode a cardinalidade; ordemServicoId nunca entra como dimensao, so
    /// como atributo de span e campo de log.
    /// </summary>
    public static class Stages
    {
        public const string Inbox = "inbox";
        public const string Outbox = "outbox";
        public const string Payment = "payment";
        public const string Saga = "saga";
    }

    public static class Integrations
    {
        public const string Cadastro = "cadastro";
        public const string Estoque = "estoque";
        public const string Sqs = "sqs";
        public const string Database = "database";
        public const string PaymentMock = "payment_mock";
    }

    public static class Operations
    {
        public const string Publish = "publish";
        public const string Receive = "receive";
        public const string Consume = "consume";
        public const string Process = "process";
        public const string Compensate = "compensate";
        public const string Query = "query";
        public const string Persist = "persist";
    }

    public static class Reasons
    {
        public const string SqsPublishFailed = "sqs_publish_failed";
        public const string InboxProcessingFailed = "inbox_processing_failed";
        public const string PaymentGatewayFailed = "payment_gateway_failed";
        public const string PaymentAttemptsExhausted = "payment_attempts_exhausted";
        public const string CompensationFailed = "compensation_failed";
    }
}

/// <summary>
/// Metricas de negocio do microsservico de Ordens.
/// Sao sinais operacionais best-effort: se o processo morrer entre o commit e o
/// flush, a transacao existe e a metrica nao. O banco e os SagaSnapshots
/// continuam sendo a fonte oficial dos estados.
/// </summary>
public sealed class OficinaBusinessMetrics : IOrdensBusinessMetrics
{
    private readonly Counter<long> _ordensCriadas;
    private readonly Counter<long> _transicoes;
    private readonly Histogram<double> _duracaoStatus;
    private readonly Counter<long> _falhasProcessamento;
    private readonly Counter<long> _falhasIntegracao;

    public OficinaBusinessMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(OficinaTelemetry.MeterName);

        _ordensCriadas = meter.CreateCounter<long>(
            "oficina.os.created",
            unit: "{ordem}",
            description: "Ordens de servico criadas.");

        _transicoes = meter.CreateCounter<long>(
            "oficina.os.status.transitions",
            unit: "{transicao}",
            description: "Transicoes de estado da saga da ordem de servico.");

        _duracaoStatus = meter.CreateHistogram<double>(
            "oficina.os.status.duration",
            unit: "s",
            description: "Tempo de permanencia em cada estado da saga.");

        _falhasProcessamento = meter.CreateCounter<long>(
            "oficina.os.processing.failures",
            unit: "{falha}",
            description: "Falhas de processamento da ordem de servico.");

        _falhasIntegracao = meter.CreateCounter<long>(
            "oficina.integration.failures",
            unit: "{falha}",
            description: "Falhas de integracao com dependencias externas.");
    }

    public void OrdemCriada() => _ordensCriadas.Add(1);

    public void Transicao(string fromStatus, string toStatus, string result, double? duracaoSegundos)
    {
        _transicoes.Add(
            1,
            new KeyValuePair<string, object?>("from_status", fromStatus),
            new KeyValuePair<string, object?>("to_status", toStatus),
            new KeyValuePair<string, object?>("result", result));

        if (duracaoSegundos is not null)
        {
            _duracaoStatus.Record(
                duracaoSegundos.Value,
                new KeyValuePair<string, object?>("status", fromStatus));
        }
    }

    public void FalhaProcessamento(string stage, string reason)
        => _falhasProcessamento.Add(
            1,
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("reason", reason));

    public void FalhaIntegracao(string integration, string operation)
        => _falhasIntegracao.Add(
            1,
            new KeyValuePair<string, object?>("integration", integration),
            new KeyValuePair<string, object?>("operation", operation));
}

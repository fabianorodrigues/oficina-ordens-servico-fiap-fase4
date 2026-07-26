using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using Oficina.OrdensServico.Infrastructure.Messaging;
using Oficina.OrdensServico.Infrastructure.Observability;

namespace Oficina.OrdensServico.UnitTests;

public class EnvelopeComContextoDeTraceTests
{
    [Fact]
    public void Deve_injetar_traceparent_quando_existe_activity()
    {
        using var listener = Ouvir();
        using var source = new ActivitySource(OficinaTelemetry.ActivitySourceName);
        using var activity = source.StartActivity("origem");
        Assert.NotNull(activity);

        var body = MessageJson.Envelope(
            OrdensMessageTypes.EstoqueReservado,
            Guid.NewGuid(),
            "correlacao-1",
            causationId: null,
            new EstoqueReservadoPayload(Guid.NewGuid(), false));

        var envelope = MessageJson.ParseAndValidate(body);
        Assert.NotNull(envelope.Traceparent);
        Assert.Contains(activity!.TraceId.ToHexString(), envelope.Traceparent);
    }

    [Fact]
    public void Deve_omitir_traceparent_quando_nao_existe_activity()
    {
        Assert.Null(Activity.Current);

        var body = MessageJson.Envelope(
            OrdensMessageTypes.EstoqueReservado,
            Guid.NewGuid(),
            "correlacao-2",
            causationId: null,
            new EstoqueReservadoPayload(Guid.NewGuid(), false));

        Assert.Null(MessageJson.ParseAndValidate(body).Traceparent);
    }

    [Fact]
    public void Deve_aceitar_envelope_antigo_sem_campos_de_trace()
    {
        // Retrocompatibilidade: mensagem em voo durante a migracao nao tem os
        // campos novos e precisa continuar valida.
        var body = """
        {"messageId":"11111111-1111-1111-1111-111111111111","messageType":"EstoqueReservado",
         "schemaVersion":1,"occurredAtUtc":"2026-07-01T00:00:00+00:00","correlationId":"legado",
         "causationId":null,"ordemServicoId":"22222222-2222-2222-2222-222222222222",
         "payload":{"reservaId":"33333333-3333-3333-3333-333333333333","duplicada":false}}
        """;

        var envelope = MessageJson.ParseAndValidate(body);

        Assert.Equal("legado", envelope.CorrelationId);
        Assert.Null(envelope.Traceparent);
        Assert.Null(envelope.Tracestate);
    }

    [Fact]
    public void WithTraceContext_deve_preservar_payload_e_propriedades_desconhecidas()
    {
        var original = """
        {"messageId":"11111111-1111-1111-1111-111111111111","messageType":"EstoqueReservado",
         "schemaVersion":1,"occurredAtUtc":"2026-07-01T00:00:00+00:00","correlationId":"c-1",
         "causationId":null,"ordemServicoId":"22222222-2222-2222-2222-222222222222",
         "payload":{"reservaId":"33333333-3333-3333-3333-333333333333","duplicada":false,"aninhado":{"x":[1,2,3]}},
         "campoDesconhecido":"deve-sobreviver"}
        """;

        var atualizado = MessageJson.WithTraceContext(
            original,
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            "vendor=abc");

        var antes = JsonNode.Parse(original)!.AsObject();
        var depois = JsonNode.Parse(atualizado)!.AsObject();

        // Equivalencia estrutural do payload, nao igualdade byte a byte.
        Assert.True(JsonNode.DeepEquals(antes["payload"], depois["payload"]));
        Assert.Equal("deve-sobreviver", depois["campoDesconhecido"]!.GetValue<string>());
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", depois["traceparent"]!.GetValue<string>());
        Assert.Equal("vendor=abc", depois["tracestate"]!.GetValue<string>());
    }

    [Fact]
    public void WithTraceContext_deve_devolver_o_corpo_intacto_sem_traceparent()
    {
        var original = """{"messageId":"1","payload":{}}""";

        Assert.Same(original, MessageJson.WithTraceContext(original, null, null));
    }

    private static ActivityListener Ouvir()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == OficinaTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

public class MessagingTelemetryTests
{
    private const string Traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    [Fact]
    public void Consumo_deve_herdar_o_contexto_do_span_de_envio()
    {
        // O parent do Consumer tem de ser o contexto que o receiver transferiu
        // dos MessageAttributes, e nao o contexto da criacao do Outbox.
        using var listener = Ouvir();
        var inbox = new InboxMessage(
            Guid.NewGuid(),
            OrdensMessageTypes.ReservarEstoque,
            Guid.NewGuid(),
            "correlacao-3",
            CorpoComTrace(Traceparent));

        using var activity = MessagingTelemetry.StartInboxConsume(inbox, "oficina-ordens-eventos.fifo");

        Assert.NotNull(activity);
        Assert.Equal(ActivityKind.Consumer, activity!.Kind);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", activity.TraceId.ToHexString());
        Assert.Equal("00f067aa0ba902b7", activity.ParentSpanId.ToHexString());
    }

    [Fact]
    public void Consumo_deve_iniciar_trace_novo_quando_a_mensagem_nao_tem_contexto()
    {
        using var listener = Ouvir();
        var inbox = new InboxMessage(
            Guid.NewGuid(),
            OrdensMessageTypes.ReservarEstoque,
            Guid.NewGuid(),
            "correlacao-4",
            CorpoComTrace(null));

        using var activity = MessagingTelemetry.StartInboxConsume(inbox, "oficina-ordens-eventos.fifo");

        Assert.NotNull(activity);
        Assert.Equal(default, activity!.ParentSpanId);
    }

    [Fact]
    public void Despacho_deve_ser_internal_para_nao_duplicar_o_span_da_instrumentacao_aws()
    {
        using var listener = Ouvir();
        var outbox = new OutboxMessage(
            Guid.NewGuid(),
            OrdensMessageTypes.EstoqueReservado,
            Guid.NewGuid(),
            "correlacao-5",
            causationId: null,
            CorpoComTrace(Traceparent));

        using var activity = MessagingTelemetry.StartOutboxDispatch(outbox, "oficina-ordens-eventos.fifo");

        Assert.NotNull(activity);
        Assert.Equal(ActivityKind.Internal, activity!.Kind);
        Assert.Equal(OficinaTelemetry.OutboxDispatchActivity, activity.OperationName);
        Assert.Equal("oficina-ordens-eventos.fifo", activity.GetTagItem(OficinaTelemetry.Attributes.DestinationName));
    }

    [Fact]
    public void Atributos_da_mensagem_devem_conter_somente_dados_de_negocio()
    {
        // traceparent e tracestate sao injetados pela instrumentacao AWS. Se
        // aparecessem aqui, seriam sobrescritos ou duplicados.
        var outbox = new OutboxMessage(
            Guid.NewGuid(),
            OrdensMessageTypes.EstoqueReservado,
            Guid.NewGuid(),
            "correlacao-6",
            causationId: "77777777-7777-7777-7777-777777777777",
            CorpoComTrace(null));

        var atributos = MessagingTelemetry.BusinessAttributes(outbox);

        Assert.Equal(
            ["causationId", "correlationId", "messageType", "ordemServicoId"],
            atributos.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("traceparent", atributos.Keys);
        Assert.DoesNotContain("tracestate", atributos.Keys);
    }

    [Fact]
    public void Deve_ler_o_contexto_dos_atributos_da_mensagem_recebida()
    {
        var message = new Message
        {
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["traceparent"] = new() { DataType = "String", StringValue = Traceparent },
                ["tracestate"] = new() { DataType = "String", StringValue = "vendor=abc" }
            }
        };

        var (traceparent, tracestate) = MessagingTelemetry.ReadTraceContext(message);

        Assert.Equal(Traceparent, traceparent);
        Assert.Equal("vendor=abc", tracestate);
    }

    [Fact]
    public void Deve_tolerar_mensagem_sem_atributos()
    {
        var (traceparent, tracestate) = MessagingTelemetry.ReadTraceContext(new Message());

        Assert.Null(traceparent);
        Assert.Null(tracestate);
    }

    [Fact]
    public void Deve_ignorar_corpo_invalido_sem_lancar()
    {
        // Telemetria nunca pode transformar um corpo malformado numa segunda
        // falha: o tratamento e do fluxo de negocio.
        using var listener = Ouvir();
        var inbox = new InboxMessage(
            Guid.NewGuid(),
            OrdensMessageTypes.ReservarEstoque,
            Guid.NewGuid(),
            "correlacao-7",
            "nao-e-json");

        var excecao = Record.Exception(() => MessagingTelemetry.StartInboxConsume(inbox, "fila")?.Dispose());

        Assert.Null(excecao);
    }

    private static string CorpoComTrace(string? traceparent)
    {
        var envelope = new
        {
            messageId = Guid.NewGuid(),
            messageType = OrdensMessageTypes.ReservarEstoque,
            schemaVersion = 1,
            occurredAtUtc = DateTimeOffset.UtcNow,
            correlationId = "correlacao",
            causationId = (string?)null,
            ordemServicoId = Guid.NewGuid(),
            payload = new { chaveOperacao = "chave", itens = Array.Empty<object>() },
            traceparent,
            tracestate = (string?)null
        };
        return JsonSerializer.Serialize(envelope, MessageJson.Options);
    }

    private static ActivityListener Ouvir()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == OficinaTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Oficina.OrdensServico.Infrastructure.Observability;
using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.UnitTests;

public class SagaTransitionBufferTests
{
    [Fact]
    public void Deve_emitir_somente_depois_do_flush()
    {
        // O flush representa o pos-commit: emitir no momento da transicao
        // contaria de novo a cada reprocessamento do Inbox apos rollback.
        using var coletor = new ColetorDeMetricas();
        var buffer = new SagaTransitionBuffer();

        buffer.Record(
            StatusSagaOrdemServico.PagamentoPendente,
            StatusSagaOrdemServico.PagamentoAprovado,
            "approved",
            DateTimeOffset.UtcNow.AddSeconds(-4));

        Assert.Empty(coletor.Transicoes);

        buffer.Flush(coletor.Metrics);

        var transicao = Assert.Single(coletor.Transicoes);
        Assert.Equal("PagamentoPendente", transicao.From);
        Assert.Equal("PagamentoAprovado", transicao.To);
        Assert.Equal("approved", transicao.Result);
    }

    [Fact]
    public void Deve_descartar_sem_emitir_quando_a_transacao_e_revertida()
    {
        using var coletor = new ColetorDeMetricas();
        var buffer = new SagaTransitionBuffer();

        buffer.Record(
            StatusSagaOrdemServico.ReservaPendente,
            StatusSagaOrdemServico.Reservada,
            "reserved",
            DateTimeOffset.UtcNow.AddSeconds(-1));
        buffer.Discard();
        buffer.Flush(coletor.Metrics);

        Assert.Empty(coletor.Transicoes);
    }

    [Fact]
    public void Deve_esvaziar_o_buffer_apos_o_flush_para_nao_reemitir()
    {
        using var coletor = new ColetorDeMetricas();
        var buffer = new SagaTransitionBuffer();

        buffer.Record(
            StatusSagaOrdemServico.ReservaPendente,
            StatusSagaOrdemServico.Reservada,
            "reserved",
            DateTimeOffset.UtcNow.AddSeconds(-1));
        buffer.Flush(coletor.Metrics);
        buffer.Flush(coletor.Metrics);

        Assert.Single(coletor.Transicoes);
    }

    [Fact]
    public void Duracao_nunca_pode_ser_negativa()
    {
        // Relogio externo, atraso de entrega ou mensagem antiga produziriam
        // duracao negativa se o instante viesse do OccurredAtUtc da mensagem.
        var transicao = new SagaTransition(
            StatusSagaOrdemServico.PagamentoPendente,
            StatusSagaOrdemServico.PagamentoAprovado,
            "approved",
            PreviousStateEnteredAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            TransitionAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(0d, transicao.DurationSeconds);
    }

    [Fact]
    public void Duracao_deve_medir_a_permanencia_no_estado_anterior()
    {
        var agora = DateTimeOffset.UtcNow;
        var transicao = new SagaTransition(
            StatusSagaOrdemServico.ReservaPendente,
            StatusSagaOrdemServico.Reservada,
            "reserved",
            PreviousStateEnteredAtUtc: agora.AddSeconds(-30),
            TransitionAtUtc: agora);

        Assert.InRange(transicao.DurationSeconds, 29.5d, 30.5d);
    }
}

public class OficinaBusinessMetricsTests
{
    [Fact]
    public void Deve_registrar_duracao_com_dimensao_do_estado_anterior()
    {
        using var coletor = new ColetorDeMetricas();

        coletor.Metrics.Transicao("ReservaPendente", "Reservada", "reserved", 12.5d);

        var duracao = Assert.Single(coletor.Duracoes);
        Assert.Equal(12.5d, duracao.Value);
        Assert.Equal("ReservaPendente", duracao.Status);
    }

    [Fact]
    public void Deve_omitir_duracao_quando_nao_informada()
    {
        using var coletor = new ColetorDeMetricas();

        coletor.Metrics.Transicao("ReservaPendente", "Reservada", "reserved", null);

        Assert.Single(coletor.Transicoes);
        Assert.Empty(coletor.Duracoes);
    }

    [Fact]
    public void Deve_contar_ordem_criada()
    {
        using var coletor = new ColetorDeMetricas();

        coletor.Metrics.OrdemCriada();

        Assert.Equal(1, coletor.OrdensCriadas);
    }

    [Fact]
    public void Deve_usar_dimensoes_de_conjunto_fechado_nas_falhas()
    {
        using var coletor = new ColetorDeMetricas();

        coletor.Metrics.FalhaIntegracao(OficinaTelemetry.Integrations.Sqs, OficinaTelemetry.Operations.Publish);
        coletor.Metrics.FalhaProcessamento(OficinaTelemetry.Stages.Outbox, OficinaTelemetry.Reasons.SqsPublishFailed);

        Assert.Equal(("sqs", "publish"), Assert.Single(coletor.FalhasIntegracao));
        Assert.Equal(("outbox", "sqs_publish_failed"), Assert.Single(coletor.FalhasProcessamento));
    }

    [Fact]
    public void As_dimensoes_nao_podem_conter_o_identificador_da_ordem()
    {
        // ordemServicoId em dimensao de metrica explodiria a cardinalidade: ele
        // vive apenas como atributo de span e campo de log.
        using var coletor = new ColetorDeMetricas();

        coletor.Metrics.Transicao("ReservaPendente", "Reservada", "reserved", 1d);

        Assert.DoesNotContain("ordemServicoId", coletor.TodasAsDimensoes);
        Assert.DoesNotContain("oficina.ordem.id", coletor.TodasAsDimensoes);
    }
}

/// <summary>
/// Observa os instrumentos reais do Meter pelo MeterListener, sem depender de
/// exporter nem de rede.
/// </summary>
internal sealed class ColetorDeMetricas : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ServiceProvider _provider;
    private readonly List<string> _dimensoes = [];

    public ColetorDeMetricas()
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = _provider.GetRequiredService<IMeterFactory>();
        Metrics = new OficinaBusinessMetrics(factory);

        _listener = new MeterListener
        {
            // Filtrar por nome do Meter nao basta: classes de teste rodam em
            // paralelo e todas criam um Meter com o mesmo nome, entao as medicoes
            // de um teste apareceriam no coletor de outro. O Scope identifica a
            // factory desta instancia.
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OficinaTelemetry.MeterName &&
                    ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<long>(OnLong);
        _listener.SetMeasurementEventCallback<double>(OnDouble);
        _listener.Start();
    }

    public OficinaBusinessMetrics Metrics { get; }
    public int OrdensCriadas { get; private set; }
    public List<(string From, string To, string Result)> Transicoes { get; } = [];
    public List<(double Value, string Status)> Duracoes { get; } = [];
    public List<(string Stage, string Reason)> FalhasProcessamento { get; } = [];
    public List<(string Integration, string Operation)> FalhasIntegracao { get; } = [];
    public IReadOnlyList<string> TodasAsDimensoes => _dimensoes;

    private void OnLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var mapa = Mapear(tags);
        switch (instrument.Name)
        {
            case "oficina.os.created":
                OrdensCriadas += (int)measurement;
                break;
            case "oficina.os.status.transitions":
                Transicoes.Add((mapa["from_status"], mapa["to_status"], mapa["result"]));
                break;
            case "oficina.os.processing.failures":
                FalhasProcessamento.Add((mapa["stage"], mapa["reason"]));
                break;
            case "oficina.integration.failures":
                FalhasIntegracao.Add((mapa["integration"], mapa["operation"]));
                break;
        }
    }

    private void OnDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var mapa = Mapear(tags);
        if (instrument.Name == "oficina.os.status.duration")
        {
            Duracoes.Add((measurement, mapa["status"]));
        }
    }

    private Dictionary<string, string> Mapear(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var mapa = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            mapa[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            _dimensoes.Add(tag.Key);
        }

        return mapa;
    }

    public void Dispose()
    {
        _listener.Dispose();
        _provider.Dispose();
    }
}

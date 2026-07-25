using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Oficina.OrdensServico.Application;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Domain.Ordens;
using Oficina.OrdensServico.Infrastructure.Messaging;
using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.UnitTests;

public class FluxoDistribuidoOrdensTests
{
    private static Orcamento OrcamentoComMateriais(Guid ordemServicoId, params (TipoMaterial Tipo, Guid Id, int Quantidade)[] materiais)
    {
        var orcamento = new Orcamento(ordemServicoId, 250m);
        orcamento.DefinirItensMaterial(materiais.Select(x =>
            new OrcamentoItemMaterial(x.Tipo, x.Id, x.Quantidade, 50m, "material")));
        return orcamento;
    }

    [Fact]
    public void Chave_de_pagamento_deve_ser_deterministica_por_ordem()
    {
        var ordemServicoId = Guid.NewGuid();

        var chave = FluxoDistribuidoOrdens.ChavePagamento(ordemServicoId);

        // A chave e o indice unico que impede dois pagamentos para a mesma ordem.
        Assert.Equal($"ordem-servico:{ordemServicoId}:pagamento", chave);
        Assert.Equal(chave, FluxoDistribuidoOrdens.ChavePagamento(ordemServicoId));
    }

    [Fact]
    public void Comando_de_reserva_deve_seguir_o_contrato_de_mensagem()
    {
        var ordemServicoId = Guid.NewGuid();
        var pecaId = Guid.NewGuid();
        var orcamento = OrcamentoComMateriais(ordemServicoId, (TipoMaterial.Peca, pecaId, 2));

        var mensagem = FluxoDistribuidoOrdens.CriarReserva(orcamento, "correlacao-1", null);

        Assert.Equal(OrdensMessageTypes.ReservarEstoque, mensagem.MessageType);
        Assert.Equal(ordemServicoId, mensagem.OrdemServicoId);
        Assert.Equal("correlacao-1", mensagem.CorrelationId);

        var envelope = MessageJson.ParseAndValidate(mensagem.Body);
        var payload = envelope.Payload.Deserialize<ReservarEstoquePayload>(MessageJson.Options)!;
        Assert.Equal($"ordem-servico:{ordemServicoId}:reserva", payload.ChaveOperacao);
        Assert.Equal(pecaId, payload.Itens[0].MaterialId);
        Assert.Equal(2, payload.Itens[0].Quantidade);
    }

    [Fact]
    public void Reserva_deve_ordenar_os_itens_por_tipo_e_material()
    {
        var ordemServicoId = Guid.NewGuid();
        var pecaId = Guid.NewGuid();
        var insumoId = Guid.NewGuid();
        var orcamento = OrcamentoComMateriais(ordemServicoId,
            (TipoMaterial.Insumo, insumoId, 1),
            (TipoMaterial.Peca, pecaId, 3));

        var mensagem = FluxoDistribuidoOrdens.CriarReserva(orcamento, "correlacao-1", null);

        var payload = MessageJson.ParseAndValidate(mensagem.Body).Payload
            .Deserialize<ReservarEstoquePayload>(MessageJson.Options)!;

        // Ordenacao estavel mantem o corpo da mensagem identico entre execucoes,
        // o que e o que torna a deduplicacao por conteudo confiavel.
        Assert.Equal((int)TipoMaterial.Peca, payload.Itens[0].TipoMaterial);
        Assert.Equal((int)TipoMaterial.Insumo, payload.Itens[1].TipoMaterial);
    }

    [Fact]
    public void Nova_tentativa_deve_gerar_chave_de_operacao_distinta()
    {
        var ordemServicoId = Guid.NewGuid();
        var orcamento = OrcamentoComMateriais(ordemServicoId, (TipoMaterial.Peca, Guid.NewGuid(), 1));

        var primeira = FluxoDistribuidoOrdens.CriarReserva(orcamento, "c1", null);
        var reprocessada = FluxoDistribuidoOrdens.CriarReserva(orcamento, "c1", null, novaTentativa: true);

        var chaveOriginal = MessageJson.ParseAndValidate(primeira.Body).Payload
            .Deserialize<ReservarEstoquePayload>(MessageJson.Options)!.ChaveOperacao;
        var chaveNova = MessageJson.ParseAndValidate(reprocessada.Body).Payload
            .Deserialize<ReservarEstoquePayload>(MessageJson.Options)!.ChaveOperacao;

        // Sem chave nova, o Estoque trataria o reprocessamento como duplicata e
        // devolveria a reserva antiga em vez de refazer o trabalho.
        Assert.NotEqual(chaveOriginal, chaveNova);
        Assert.StartsWith($"ordem-servico:{ordemServicoId}:reserva", chaveNova);
    }

    [Fact]
    public void Comando_de_liberacao_deve_carregar_a_reserva_e_a_causa()
    {
        var ordemServicoId = Guid.NewGuid();
        var reservaId = Guid.NewGuid();

        var mensagem = FluxoDistribuidoOrdens.CriarLiberacao(ordemServicoId, reservaId, "correlacao-1", "causa-1");

        Assert.Equal(OrdensMessageTypes.LiberarReservaEstoque, mensagem.MessageType);
        Assert.Equal("causa-1", mensagem.CausationId);

        var payload = MessageJson.ParseAndValidate(mensagem.Body).Payload
            .Deserialize<LiberarReservaEstoquePayload>(MessageJson.Options)!;
        Assert.Equal(reservaId, payload.ReservaId);
    }
}

public class ComposicaoDaAplicacaoOrdensTests
{
    [Fact]
    public void Registro_da_aplicacao_deve_expor_validadores_e_notificador()
    {
        var services = new ServiceCollection();
        services.AddOrdensApplication();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<INotificadorCliente>());
        Assert.NotNull(provider.GetRequiredService<FluentValidation.IValidator<Application.Contracts.AbrirOrdemServicoRequest>>());
    }

    [Fact]
    public async Task Notificador_padrao_deve_ser_inerte()
    {
        var services = new ServiceCollection();
        services.AddOrdensApplication();
        using var provider = services.BuildServiceProvider();

        var notificador = provider.GetRequiredService<INotificadorCliente>();

        // A notificacao real esta fora do escopo: o registro padrao existe para
        // que o fluxo nao dependa de um canal que ainda nao existe.
        await notificador.NotificarOrcamentoCriado(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        await notificador.NotificarOrcamentoRecusado(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
    }
}

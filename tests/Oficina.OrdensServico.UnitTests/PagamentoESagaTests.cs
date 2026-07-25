using Microsoft.Extensions.Configuration;
using Oficina.OrdensServico.Domain.Ordens;
using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.UnitTests;

public class PagamentoOrdemTests
{
    private static PagamentoOrdem Novo() => new(Guid.NewGuid(), "ordem-servico:1:pagamento");

    [Fact]
    public void Deve_recusar_ordem_de_servico_vazia()
    {
        Assert.Throws<ArgumentException>(() => new PagamentoOrdem(Guid.Empty, "chave"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Deve_recusar_chave_de_idempotencia_vazia(string chave)
    {
        Assert.Throws<ArgumentException>(() => new PagamentoOrdem(Guid.NewGuid(), chave));
    }

    [Fact]
    public void Deve_nascer_pendente_e_processavel()
    {
        var pagamento = Novo();

        Assert.Equal(StatusPagamentoOrdem.Pendente, pagamento.Status);
        Assert.Equal("Mock", pagamento.Provider);
        Assert.Equal("Payment", pagamento.OperationType);
        Assert.Equal(0, pagamento.AttemptCount);
        Assert.True(pagamento.PodeProcessar(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Nao_deve_processar_enquanto_estiver_travado_por_outro_worker()
    {
        var pagamento = Novo();
        var agora = DateTimeOffset.UtcNow;

        pagamento.Claim("worker-1", agora.AddMinutes(5));

        Assert.False(pagamento.PodeProcessar(agora));
        Assert.Equal(1, pagamento.AttemptCount);
        // Depois que o lock expira, outro worker pode assumir: e isso que evita
        // que um processo morto deixe o pagamento parado para sempre.
        Assert.True(pagamento.PodeProcessar(agora.AddMinutes(6)));
    }

    [Fact]
    public void Nao_deve_processar_antes_da_proxima_tentativa_agendada()
    {
        var pagamento = Novo();
        pagamento.Reagendar("Pagamento pendente.");

        Assert.False(pagamento.PodeProcessar(DateTimeOffset.UtcNow));
        Assert.True(pagamento.PodeProcessar(DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    [Fact]
    public void Nao_deve_processar_quando_ja_concluido()
    {
        var pagamento = Novo();
        pagamento.MarcarAprovado("mock-1");

        Assert.False(pagamento.PodeProcessar(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("mock-ordem-servico-1-pagamento", "Mock")]
    [InlineData("ext-98765", "ExternalPaymentApi")]
    public void Aprovacao_deve_deduzir_o_provider_pelo_identificador_externo(string externo, string providerEsperado)
    {
        var pagamento = Novo();

        pagamento.MarcarAprovado(externo);

        Assert.Equal(StatusPagamentoOrdem.Aprovado, pagamento.Status);
        Assert.Equal(providerEsperado, pagamento.Provider);
        Assert.Equal(externo, pagamento.PagamentoExternoId);
        Assert.Null(pagamento.LockedUntilUtc);
        Assert.Null(pagamento.LockedBy);
    }

    [Fact]
    public void Recusa_deve_guardar_o_motivo_e_liberar_o_lock()
    {
        var pagamento = Novo();
        pagamento.Claim("worker-1", DateTimeOffset.UtcNow.AddMinutes(1));

        pagamento.MarcarRecusado("ext-1", "Cartao sem limite.");

        Assert.Equal(StatusPagamentoOrdem.Recusado, pagamento.Status);
        Assert.Equal("Cartao sem limite.", pagamento.LastError);
        Assert.Null(pagamento.LockedUntilUtc);
    }

    [Fact]
    public void Compensacao_deve_ser_idempotente()
    {
        var pagamento = Novo();
        pagamento.MarcarAprovado("mock-1");

        pagamento.MarcarCompensado("mock-compensation-1");
        var primeiraData = pagamento.CompensatedAtUtc;

        pagamento.MarcarCompensado("mock-compensation-2");

        Assert.Equal(StatusPagamentoOrdem.Compensado, pagamento.Status);
        Assert.Equal("Compensation", pagamento.OperationType);
        // O segundo pedido nao sobrescreve a compensacao ja registrada.
        Assert.Equal("mock-compensation-1", pagamento.CompensacaoExternaId);
        Assert.Equal(primeiraData, pagamento.CompensatedAtUtc);
    }

    [Fact]
    public void Reagendamento_deve_crescer_com_as_tentativas_ate_o_teto()
    {
        var pagamento = Novo();
        for (var i = 0; i < 20; i++)
        {
            pagamento.Claim("worker-1", DateTimeOffset.UtcNow.AddMinutes(1));
        }

        pagamento.Reagendar("indisponivel");

        var atraso = pagamento.NextAttemptAtUtc!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(atraso.TotalSeconds, 50, 61);
    }

    [Fact]
    public void Falha_final_deve_encerrar_o_pagamento()
    {
        var pagamento = Novo();

        pagamento.MarcarFalhaFinal(new string('x', 900));

        Assert.Equal(StatusPagamentoOrdem.Falhou, pagamento.Status);
        Assert.Equal(500, pagamento.LastError!.Length);
        Assert.False(pagamento.PodeProcessar(DateTimeOffset.UtcNow));
    }
}

public class SagaOrdemServicoTests
{
    [Fact]
    public void Deve_recusar_ordem_de_servico_vazia()
    {
        Assert.Throws<ArgumentException>(() => new SagaOrdemServico(Guid.Empty));
    }

    [Fact]
    public void Deve_nascer_com_pagamento_pendente()
    {
        var saga = new SagaOrdemServico(Guid.NewGuid());

        Assert.Equal(StatusSagaOrdemServico.PagamentoPendente, saga.Status);
        Assert.Null(saga.ReservaId);
        Assert.Null(saga.LastError);
    }

    [Fact]
    public void Caminho_feliz_deve_terminar_em_concluida_com_reserva_registrada()
    {
        var saga = new SagaOrdemServico(Guid.NewGuid());
        var reservaId = Guid.NewGuid();

        saga.PagamentoAprovado();
        Assert.Equal(StatusSagaOrdemServico.PagamentoAprovado, saga.Status);

        saga.ReservaPendente();
        Assert.Equal(StatusSagaOrdemServico.ReservaPendente, saga.Status);

        saga.Reservada(reservaId);
        Assert.Equal(StatusSagaOrdemServico.Reservada, saga.Status);
        Assert.Equal(reservaId, saga.ReservaId);

        // Reservada e Concluida acontecem na mesma unidade de trabalho ao
        // consumir EstoqueReservado: o estado final observavel e Concluida.
        saga.Concluir();
        Assert.Equal(StatusSagaOrdemServico.Concluida, saga.Status);
    }

    [Fact]
    public void Recusa_de_reserva_deve_guardar_o_motivo()
    {
        var saga = new SagaOrdemServico(Guid.NewGuid());

        saga.ReservaRecusada("Saldo insuficiente.");

        Assert.Equal(StatusSagaOrdemServico.ReservaRecusada, saga.Status);
        Assert.Equal("Saldo insuficiente.", saga.LastError);
    }

    [Fact]
    public void Compensacao_deve_percorrer_pendente_e_compensada()
    {
        var saga = new SagaOrdemServico(Guid.NewGuid());
        saga.Reservada(Guid.NewGuid());

        saga.CompensacaoPendente();
        Assert.Equal(StatusSagaOrdemServico.CompensacaoPendente, saga.Status);

        saga.Compensada();
        Assert.Equal(StatusSagaOrdemServico.Compensada, saga.Status);
    }

    [Fact]
    public void Falha_de_compensacao_deve_ser_registrada_e_truncada()
    {
        var saga = new SagaOrdemServico(Guid.NewGuid());

        saga.CompensacaoFalhou(new string('e', 900));

        Assert.Equal(StatusSagaOrdemServico.CompensacaoFalhou, saga.Status);
        Assert.Equal(500, saga.LastError!.Length);
    }

    [Fact]
    public void Cada_transicao_deve_atualizar_o_carimbo_de_tempo()
    {
        var saga = new SagaOrdemServico(Guid.NewGuid());
        var inicial = saga.UpdatedAtUtc;

        Thread.Sleep(5);
        saga.PagamentoAprovado();

        Assert.True(saga.UpdatedAtUtc > inicial);
    }
}

public class SagaSnapshotTests
{
    [Fact]
    public void Deve_guardar_a_transicao_com_o_evento_que_a_disparou()
    {
        var sagaId = Guid.NewGuid();
        var ordemServicoId = Guid.NewGuid();
        var messageId = Guid.NewGuid().ToString();

        var snapshot = new SagaSnapshot(
            sagaId, ordemServicoId,
            StatusSagaOrdemServico.ReservaPendente,
            StatusSagaOrdemServico.Reservada,
            "EstoqueReservado", messageId, "Reserva confirmada pelo Estoque.");

        Assert.Equal(sagaId, snapshot.SagaId);
        Assert.Equal(ordemServicoId, snapshot.OrdemServicoId);
        Assert.Equal(StatusSagaOrdemServico.ReservaPendente, snapshot.PreviousState);
        Assert.Equal(StatusSagaOrdemServico.Reservada, snapshot.NewState);
        Assert.Equal("EstoqueReservado", snapshot.EventType);
        Assert.Equal(messageId, snapshot.TriggerMessageId);
    }
}

public class MockPagamentoGatewayTests
{
    private static MockPagamentoGateway Gateway(string? comportamento)
    {
        var valores = new Dictionary<string, string?>();
        if (comportamento is not null) valores["Payments:MockBehavior"] = comportamento;
        return new MockPagamentoGateway(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
    }

    private static PagamentoGatewayRequest Request() =>
        new(Guid.NewGuid(), 200m, "ordem-servico:1:pagamento", "correlacao-1");

    [Theory]
    [InlineData("Approved", ResultadoPagamentoStatus.Aprovado)]
    [InlineData("aprovado", ResultadoPagamentoStatus.Aprovado)]
    [InlineData("Rejected", ResultadoPagamentoStatus.Recusado)]
    [InlineData("recusado", ResultadoPagamentoStatus.Recusado)]
    [InlineData("Pending", ResultadoPagamentoStatus.Pendente)]
    [InlineData("pendente", ResultadoPagamentoStatus.Pendente)]
    public async Task Deve_respeitar_o_comportamento_configurado(string comportamento, ResultadoPagamentoStatus esperado)
    {
        var resultado = await Gateway(comportamento).Processar(Request(), CancellationToken.None);

        Assert.Equal(esperado, resultado.Status);
        Assert.StartsWith("mock-", resultado.PagamentoExternoId);
    }

    [Fact]
    public async Task Sem_configuracao_o_padrao_e_aprovado()
    {
        var resultado = await Gateway(null).Processar(Request(), CancellationToken.None);

        Assert.Equal(ResultadoPagamentoStatus.Aprovado, resultado.Status);
    }

    [Fact]
    public async Task Comportamento_desconhecido_deve_reprovar()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Gateway("Talvez").Processar(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task Identificador_externo_deve_ser_estavel_para_a_mesma_chave()
    {
        var request = Request();

        var primeiro = await Gateway("Approved").Processar(request, CancellationToken.None);
        var segundo = await Gateway("Approved").Processar(request, CancellationToken.None);

        // Estabilidade e o que torna a reentrega segura: o mesmo pagamento
        // sempre recebe o mesmo identificador externo.
        Assert.Equal(primeiro.PagamentoExternoId, segundo.PagamentoExternoId);
    }

    [Fact]
    public async Task Deve_recusar_requisicao_invalida()
    {
        var gateway = Gateway("Approved");

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.Processar(
            new PagamentoGatewayRequest(Guid.Empty, 1m, "chave", "c1"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.Processar(
            new PagamentoGatewayRequest(Guid.NewGuid(), 1m, "  ", "c1"), CancellationToken.None));
    }

    [Fact]
    public async Task Compensacao_deve_devolver_identificador_derivado_do_pagamento()
    {
        var pagamentoId = Guid.NewGuid();

        var resultado = await Gateway("Approved").Compensar(
            new PagamentoCompensacaoRequest(Guid.NewGuid(), pagamentoId, "chave", "c1"), CancellationToken.None);

        Assert.True(resultado.Succeeded);
        Assert.Equal($"mock-compensation-{pagamentoId:N}", resultado.CompensacaoExternaId);
    }

    [Fact]
    public async Task Compensacao_deve_recusar_operacao_invalida()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway("Approved").Compensar(
            new PagamentoCompensacaoRequest(Guid.Empty, Guid.NewGuid(), "chave", "c1"), CancellationToken.None));
    }

    [Fact]
    public async Task Deve_respeitar_o_cancelamento()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Gateway("Approved").Processar(Request(), cts.Token));
    }
}

public class ContratoExternoPendenteTests
{
    [Fact]
    public void Mapper_pendente_deve_falhar_em_todas_as_operacoes()
    {
        // O contrato externo esta fora do escopo desta entrega: qualquer uso
        // acidental precisa falhar alto, e nao silenciosamente.
        var mapper = new PendingExternalPaymentContractMapper();

        Assert.Throws<PaymentContractPendingException>(() => mapper.CreatePaymentRequest(
            new PagamentoGatewayRequest(Guid.NewGuid(), 1m, "chave", "c1"),
            new Uri("https://webhook.example.invalid/api/webhooks/payments"),
            new PaymentIntegrationContext("c1", "chave", 1)));

        // O contrato pendente lanca de forma sincrona, antes de devolver a Task.
        Assert.Throws<PaymentContractPendingException>(
            () => { _ = mapper.ReadSubmissionResponseAsync(new HttpResponseMessage(), CancellationToken.None); });

        Assert.Throws<PaymentContractPendingException>(
            () => { _ = mapper.ReadWebhookAsync(Stream.Null, new Dictionary<string, string>(), CancellationToken.None); });
    }

    [Fact]
    public async Task Autenticador_pendente_deve_recusar_com_ou_sem_webhook_habilitado()
    {
        var desabilitado = new PendingPaymentWebhookAuthenticator(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:ExternalWebhookEnabled"] = "false"
            }).Build());

        var habilitado = new PendingPaymentWebhookAuthenticator(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:ExternalWebhookEnabled"] = "true"
            }).Build());

        var semWebhook = await desabilitado.AuthenticateAsync(
            new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        var comWebhook = await habilitado.AuthenticateAsync(
            new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.False(semWebhook.Succeeded);
        Assert.Equal("Webhook externo desabilitado.", semWebhook.FailureReason);
        Assert.False(comWebhook.Succeeded);
        Assert.Equal("Autenticacao do webhook pendente de contrato externo.", comWebhook.FailureReason);
    }

    [Fact]
    public void Resultado_de_autenticacao_deve_expor_sucesso_e_falha()
    {
        Assert.True(WebhookAuthenticationResult.Success().Succeeded);
        Assert.Equal("motivo", WebhookAuthenticationResult.Failure("motivo").FailureReason);
    }

    [Fact]
    public void Hash_estavel_deve_produzir_o_mesmo_guid_para_a_mesma_entrada()
    {
        Assert.Equal(PaymentHashing.StableGuid("chave-1"), PaymentHashing.StableGuid("chave-1"));
        Assert.NotEqual(PaymentHashing.StableGuid("chave-1"), PaymentHashing.StableGuid("chave-2"));
        Assert.Equal(64, PaymentHashing.Sha256Hex("conteudo"u8).Length);
    }
}

public class OrdemServicoTests
{
    private static OrdemServico Nova() => OrdemServico.CriarRecebida(
        Guid.NewGuid(), Guid.NewGuid(),
        new SnapshotCliente(Guid.NewGuid(), "Maria", "12345678909", "maria@example.invalid", "11999990000"),
        new SnapshotVeiculo(Guid.NewGuid(), "ABC1D23", "12345678901", "Civic", "Honda", 2022));

    [Fact]
    public void Deve_recusar_cliente_ou_veiculo_vazio()
    {
        var cliente = new SnapshotCliente(Guid.NewGuid(), "Maria", "12345678909", "maria@example.invalid", "11999990000");
        var veiculo = new SnapshotVeiculo(Guid.NewGuid(), "ABC1D23", "12345678901", "Civic", "Honda", 2022);

        Assert.Throws<ArgumentException>(() => OrdemServico.CriarRecebida(Guid.Empty, Guid.NewGuid(), cliente, veiculo));
        Assert.Throws<ArgumentException>(() => OrdemServico.CriarRecebida(Guid.NewGuid(), Guid.Empty, cliente, veiculo));
    }

    [Fact]
    public void Deve_nascer_recebida_e_nao_classificada()
    {
        var ordem = Nova();

        Assert.Equal(StatusOrdemServico.Recebida, ordem.Status);
        Assert.Equal(TipoManutencao.NaoClassificada, ordem.TipoManutencao);
        Assert.Equal(OrigemAtualizacaoStatusOs.Interna, ordem.OrigemUltimaAtualizacaoStatus);
    }

    [Fact]
    public void Classificacao_preventiva_exige_servicos_e_vai_para_aprovacao()
    {
        var ordem = Nova();
        var servicoId = Guid.NewGuid();

        ordem.Classificar(TipoManutencao.Preventiva, [servicoId, servicoId, Guid.Empty]);

        Assert.Equal(StatusOrdemServico.AguardandoAprovacao, ordem.Status);
        // Identificadores repetidos e vazios sao descartados na classificacao.
        Assert.Single(ordem.ItensServico);
    }

    [Fact]
    public void Classificacao_preventiva_sem_servico_deve_ser_recusada()
    {
        Assert.Throws<ArgumentException>(() => Nova().Classificar(TipoManutencao.Preventiva, []));
    }

    [Fact]
    public void Classificacao_corretiva_vai_para_diagnostico()
    {
        var ordem = Nova();

        ordem.Classificar(TipoManutencao.Corretiva);

        Assert.Equal(StatusOrdemServico.EmDiagnostico, ordem.Status);
    }

    [Fact]
    public void Nao_deve_classificar_duas_vezes()
    {
        var ordem = Nova();
        ordem.Classificar(TipoManutencao.Corretiva);

        Assert.Throws<InvalidOperationException>(() => ordem.Classificar(TipoManutencao.Preventiva, [Guid.NewGuid()]));
    }

    [Fact]
    public void Nao_deve_classificar_como_nao_classificada()
    {
        Assert.Throws<InvalidOperationException>(() => Nova().Classificar(TipoManutencao.NaoClassificada));
    }

    [Fact]
    public void Diagnostico_so_existe_em_ordem_corretiva()
    {
        var ordem = Nova();
        ordem.Classificar(TipoManutencao.Preventiva, [Guid.NewGuid()]);

        Assert.Throws<InvalidOperationException>(() => ordem.RegistrarDiagnostico("desc", [Guid.NewGuid()]));
    }

    [Fact]
    public void Diagnostico_exige_ao_menos_um_servico_identificado()
    {
        var ordem = Nova();
        ordem.Classificar(TipoManutencao.Corretiva);

        Assert.Throws<ArgumentException>(() => ordem.RegistrarDiagnostico("desc", [Guid.Empty]));
    }

    [Fact]
    public void Diagnostico_registrado_leva_para_aprovacao()
    {
        var ordem = Nova();
        ordem.Classificar(TipoManutencao.Corretiva);

        ordem.RegistrarDiagnostico("Troca de correia", [Guid.NewGuid()]);

        Assert.Equal(StatusOrdemServico.AguardandoAprovacao, ordem.Status);
        Assert.NotNull(ordem.Diagnostico);
    }

    [Fact]
    public void Vinculo_de_orcamento_deve_recusar_identificador_vazio()
    {
        Assert.Throws<ArgumentException>(() => Nova().VincularOrcamento(Guid.Empty));
    }

    [Fact]
    public void Ciclo_completo_deve_ir_de_execucao_a_entrega()
    {
        var ordem = Nova();
        ordem.Classificar(TipoManutencao.Corretiva);
        ordem.RegistrarDiagnostico("Troca de correia", [Guid.NewGuid()]);

        ordem.IniciarExecucao();
        Assert.Equal(StatusOrdemServico.EmExecucao, ordem.Status);
        Assert.NotNull(ordem.DataInicioExecucao);

        ordem.Finalizar();
        Assert.Equal(StatusOrdemServico.Finalizada, ordem.Status);
        Assert.NotNull(ordem.DataFimExecucao);

        ordem.MarcarEntregue();
        Assert.Equal(StatusOrdemServico.Entregue, ordem.Status);
    }

    [Fact]
    public void Nao_deve_iniciar_execucao_fora_de_aprovacao()
    {
        Assert.Throws<InvalidOperationException>(() => Nova().IniciarExecucao());
    }

    [Fact]
    public void Nao_deve_finalizar_nem_entregar_fora_de_ordem()
    {
        var ordem = Nova();

        Assert.Throws<InvalidOperationException>(() => ordem.Finalizar());
        Assert.Throws<InvalidOperationException>(() => ordem.MarcarEntregue());
    }

    [Fact]
    public void Compensacao_deve_devolver_a_ordem_para_espera()
    {
        var ordem = Nova();
        ordem.Classificar(TipoManutencao.Corretiva);
        ordem.RegistrarDiagnostico("Troca de correia", [Guid.NewGuid()]);
        ordem.IniciarExecucao();

        ordem.RetornarParaEsperaAposCompensacao();

        Assert.Equal(StatusOrdemServico.AguardandoAprovacao, ordem.Status);
        // Zerar o inicio de execucao evita duracao inflada quando a ordem volta
        // a ser executada depois da compensacao.
        Assert.Null(ordem.DataInicioExecucao);
    }

    [Fact]
    public void Compensacao_fora_de_execucao_ou_espera_deve_ser_recusada()
    {
        var ordem = Nova();

        Assert.Throws<InvalidOperationException>(() => ordem.RetornarParaEsperaAposCompensacao());
    }
}

using Microsoft.Extensions.Configuration;
using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.IntegrationTests;

public sealed class PagamentoMockTests
{
    [Theory]
    [InlineData("Approved", ResultadoPagamentoStatus.Aprovado)]
    [InlineData("Rejected", ResultadoPagamentoStatus.Recusado)]
    [InlineData("Pending", ResultadoPagamentoStatus.Pendente)]
    public async Task Mock_pagamento_suporta_cenarios_principais(string scenario, ResultadoPagamentoStatus expected)
    {
        var gateway = new MockPagamentoGateway(Config(("Payments:MockBehavior", scenario)));
        var request = Request();

        var first = await gateway.Processar(request, CancellationToken.None);
        var second = await gateway.Processar(request, CancellationToken.None);

        Assert.Equal(expected, first.Status);
        Assert.Equal(first.PagamentoExternoId, second.PagamentoExternoId);
    }

    [Fact]
    public async Task Mock_pagamento_retorna_referencia_estavel_sem_http()
    {
        var gateway = new MockPagamentoGateway(Config(("Payments:MockBehavior", "Approved")));
        var request = Request();

        var result = await gateway.Processar(request, CancellationToken.None);

        Assert.Equal(ResultadoPagamentoStatus.Aprovado, result.Status);
        Assert.Equal("mock-ordem-servico-teste-pagamento", result.PagamentoExternoId);
    }

    [Fact]
    public async Task Mock_compensacao_retorna_sucesso_idempotente()
    {
        var gateway = new MockPagamentoGateway(Config(("Payments:MockBehavior", "Approved")));
        var pagamentoId = Guid.NewGuid();
        var request = new PagamentoCompensacaoRequest(Guid.NewGuid(), pagamentoId, "compensacao", "correlation-test");

        var first = await gateway.Compensar(request, CancellationToken.None);
        var second = await gateway.Compensar(request, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(first.CompensacaoExternaId, second.CompensacaoExternaId);
        Assert.Equal($"mock-compensation-{pagamentoId:N}", first.CompensacaoExternaId);
    }

    [Fact]
    public void Saga_snapshot_sanitiza_payload_extenso()
    {
        var sagaId = Guid.NewGuid();
        var snapshot = new SagaSnapshot(
            sagaId,
            Guid.NewGuid(),
            StatusSagaOrdemServico.PagamentoPendente,
            StatusSagaOrdemServico.PagamentoAprovado,
            "PagamentoAprovado",
            Guid.NewGuid().ToString(),
            new string('x', 1100));

        Assert.Equal(sagaId, snapshot.SagaId);
        Assert.Equal(1000, snapshot.PayloadSummary!.Length);
    }

    private static PagamentoGatewayRequest Request() =>
        new(Guid.NewGuid(), 123.45m, "ordem-servico:teste:pagamento", "correlation-test");

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();
}

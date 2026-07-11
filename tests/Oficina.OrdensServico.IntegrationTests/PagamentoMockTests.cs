using Microsoft.Extensions.Configuration;
using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.IntegrationTests;

public sealed class PagamentoMockTests
{
    [Theory]
    [InlineData("aprovado", ResultadoPagamentoStatus.Aprovado)]
    [InlineData("recusado", ResultadoPagamentoStatus.Recusado)]
    [InlineData("pendente", ResultadoPagamentoStatus.Pendente)]
    public async Task Mock_pagamento_suporta_cenarios_principais(string scenario, ResultadoPagamentoStatus expected)
    {
        var gateway = new MockPagamentoGateway(Config(("Payments:Scenario", scenario)));
        var request = Request();

        var first = await gateway.Processar(request, CancellationToken.None);
        var second = await gateway.Processar(request, CancellationToken.None);

        Assert.Equal(expected, first.Status);
        Assert.Equal(first.PagamentoExternoId, second.PagamentoExternoId);
    }

    [Fact]
    public async Task Mock_pagamento_simula_erro_transitorio()
    {
        var gateway = new MockPagamentoGateway(Config(("Payments:Scenario", "erro-transitorio")));
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.Processar(Request(), CancellationToken.None));
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

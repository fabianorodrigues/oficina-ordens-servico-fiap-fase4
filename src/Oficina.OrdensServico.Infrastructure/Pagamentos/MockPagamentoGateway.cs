using Microsoft.Extensions.Configuration;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public sealed class MockPagamentoGateway(IConfiguration configuration) : IPagamentoGateway
{
    public Task<PagamentoGatewayResult> Processar(PagamentoGatewayRequest request, CancellationToken ct)
    {
        var scenario = configuration["Payments:Scenario"] ?? "aprovado";
        return Task.FromResult(scenario.ToLowerInvariant() switch
        {
            "recusado" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Recusado, Guid.NewGuid().ToString(), "Pagamento recusado."),
            "pendente" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Pendente, Guid.NewGuid().ToString(), null),
            _ => new PagamentoGatewayResult(ResultadoPagamentoStatus.Aprovado, Guid.NewGuid().ToString(), null)
        });
    }
}

using Microsoft.Extensions.Configuration;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public sealed class MockPagamentoGateway(IConfiguration configuration) : IPagamentoGateway
{
    public async Task<PagamentoGatewayResult> Processar(PagamentoGatewayRequest request, CancellationToken ct)
    {
        var scenario = configuration["Payments:Scenario"] ?? "aprovado";
        if (string.Equals(scenario, "timeout", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(TimeSpan.FromSeconds(configuration.GetValue("Payments:TimeoutSeconds", 5) + 1), ct);
            throw new HttpRequestException("Timeout simulado no provedor de pagamento.");
        }

        if (string.Equals(scenario, "erro-transitorio", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Erro transitorio simulado no provedor de pagamento.");

        if (string.Equals(scenario, "falha-compensacao", StringComparison.OrdinalIgnoreCase))
            return new PagamentoGatewayResult(ResultadoPagamentoStatus.Aprovado, StableExternalId(request, "compensation-failure"), "Compensacao deve falhar no cenario mock.");

        return scenario.ToLowerInvariant() switch
        {
            "recusado" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Recusado, StableExternalId(request, "rejected"), "Pagamento recusado."),
            "pendente" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Pendente, StableExternalId(request, "pending"), null),
            _ => new PagamentoGatewayResult(ResultadoPagamentoStatus.Aprovado, StableExternalId(request, "approved"), null)
        };
    }

    private static string StableExternalId(PagamentoGatewayRequest request, string suffix) =>
        $"mock-{request.ChaveIdempotencia.Replace(':', '-')}-{suffix}";
}

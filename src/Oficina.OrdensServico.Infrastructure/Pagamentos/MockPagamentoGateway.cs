using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public sealed class MockPagamentoGateway(IConfiguration configuration, ILogger<MockPagamentoGateway>? logger = null) : IPagamentoGateway
{
    public Task<PagamentoGatewayResult> Processar(PagamentoGatewayRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.OrdemServicoId == Guid.Empty)
            throw new ArgumentException("Ordem de servico invalida.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ChaveIdempotencia))
            throw new ArgumentException("Chave de idempotencia invalida.", nameof(request));

        var behavior = configuration["Payments:MockBehavior"] ?? configuration["Payments:Scenario"] ?? "Approved";
        logger?.LogInformation("Processando pagamento mock. OrdemServicoId={OrdemServicoId} Provider=Mock PaymentStatus={PaymentStatus}",
            request.OrdemServicoId, behavior);

        var result = behavior.ToLowerInvariant() switch
        {
            "rejected" or "recusado" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Recusado, StableExternalId(request), "Pagamento mock recusado."),
            "pending" or "pendente" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Pendente, StableExternalId(request), null),
            "approved" or "aprovado" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Aprovado, StableExternalId(request), null),
            _ => throw new InvalidOperationException("Payments:MockBehavior invalido.")
        };

        return Task.FromResult(result);
    }

    public Task<PagamentoCompensacaoResult> Compensar(PagamentoCompensacaoRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.OrdemServicoId == Guid.Empty || request.PagamentoId == Guid.Empty)
            throw new ArgumentException("Operacao de pagamento invalida.", nameof(request));

        logger?.LogInformation("Compensando pagamento mock. OrdemServicoId={OrdemServicoId} PaymentOperationId={PaymentOperationId} Provider=Mock",
            request.OrdemServicoId, request.PagamentoId);

        return Task.FromResult(new PagamentoCompensacaoResult(
            true,
            $"mock-compensation-{request.PagamentoId:N}",
            null));
    }

    private static string StableExternalId(PagamentoGatewayRequest request) =>
        $"mock-{request.ChaveIdempotencia.Replace(':', '-')}";
}

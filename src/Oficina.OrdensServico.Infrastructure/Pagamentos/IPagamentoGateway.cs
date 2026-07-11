namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public enum ResultadoPagamentoStatus
{
    Aprovado,
    Recusado,
    Pendente
}

public sealed record PagamentoGatewayRequest(Guid OrdemServicoId, decimal Valor, string ChaveIdempotencia, string CorrelationId);
public sealed record PagamentoGatewayResult(ResultadoPagamentoStatus Status, string? PagamentoExternoId, string? Motivo);

public interface IPagamentoGateway
{
    Task<PagamentoGatewayResult> Processar(PagamentoGatewayRequest request, CancellationToken ct);
}

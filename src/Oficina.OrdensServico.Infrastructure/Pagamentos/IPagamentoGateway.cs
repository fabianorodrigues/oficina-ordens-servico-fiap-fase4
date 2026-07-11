namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public enum ResultadoPagamentoStatus
{
    Aprovado,
    Recusado,
    Pendente,
    Estornado
}

public sealed record PagamentoGatewayRequest(Guid OrdemServicoId, decimal Valor, string ChaveIdempotencia, string CorrelationId);
public sealed record PagamentoGatewayResult(ResultadoPagamentoStatus Status, string? PagamentoExternoId, string? Motivo);
public sealed record PagamentoCompensacaoRequest(Guid OrdemServicoId, Guid PagamentoId, string ChaveIdempotencia, string CorrelationId);
public sealed record PagamentoCompensacaoResult(bool Succeeded, string? CompensacaoExternaId, string? Motivo);

public interface IPagamentoGateway
{
    Task<PagamentoGatewayResult> Processar(PagamentoGatewayRequest request, CancellationToken ct);
    Task<PagamentoCompensacaoResult> Compensar(PagamentoCompensacaoRequest request, CancellationToken ct);
}

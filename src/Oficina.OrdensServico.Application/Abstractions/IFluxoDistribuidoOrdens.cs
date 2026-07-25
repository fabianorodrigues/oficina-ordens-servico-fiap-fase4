using Oficina.OrdensServico.Domain.Ordens;

namespace Oficina.OrdensServico.Application.Abstractions;

public interface IFluxoDistribuidoOrdens
{
    Task IniciarAprovacao(Orcamento orcamento, CancellationToken ct);
    Task ForcarCompensacao(Guid ordemServicoId, string correlationId, CancellationToken ct);
    Task ReprocessarReserva(Guid ordemServicoId, string correlationId, CancellationToken ct);
}

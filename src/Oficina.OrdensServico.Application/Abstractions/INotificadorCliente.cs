namespace Oficina.OrdensServico.Application.Abstractions;

public interface INotificadorCliente
{
    Task NotificarOrcamentoCriado(Guid orcamentoId, Guid ordemServicoId, CancellationToken ct);
    Task NotificarOrcamentoRecusado(Guid orcamentoId, Guid ordemServicoId, CancellationToken ct);
}

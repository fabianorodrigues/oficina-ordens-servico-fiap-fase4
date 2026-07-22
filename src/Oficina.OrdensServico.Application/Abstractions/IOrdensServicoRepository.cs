using Oficina.OrdensServico.Domain.Oficina;

namespace Oficina.OrdensServico.Application.Abstractions;

public interface IOrdensServicoRepository
{
    Task<OrdemServico?> ObterOrdemServico(Guid id, CancellationToken ct);
    Task<IReadOnlyList<OrdemServico>> ListarOrdensServico(CancellationToken ct);
    Task<IReadOnlyList<OrdemServico>> ListarOrdensServicoPorCliente(Guid clienteId, CancellationToken ct);
    Task<Orcamento?> ObterOrcamento(Guid id, CancellationToken ct);
    Task<Orcamento?> ObterOrcamentoPorOs(Guid ordemServicoId, CancellationToken ct);
    Task<Orcamento?> ObterOrcamentoPorTokenAcaoExterna(string token, CancellationToken ct);
    void AdicionarOrdemServico(OrdemServico ordem);
    void AdicionarOrcamento(Orcamento orcamento);
    Task Salvar(CancellationToken ct);
}

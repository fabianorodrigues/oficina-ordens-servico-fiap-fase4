using Microsoft.EntityFrameworkCore;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Domain.Oficina;

namespace Oficina.OrdensServico.Infrastructure.Persistencia;

public sealed class OrdensServicoRepository(OrdensServicoDbContext db) : IOrdensServicoRepository
{
    public Task<OrdemServico?> ObterOrdemServico(Guid id, CancellationToken ct) => db.OrdensServico.Include(x => x.ItensServico).FirstOrDefaultAsync(x => x.Id == id, ct);
    public async Task<IReadOnlyList<OrdemServico>> ListarOrdensServico(CancellationToken ct) => await db.OrdensServico.OrderByDescending(x => x.DataCriacao).ToListAsync(ct);
    public async Task<IReadOnlyList<OrdemServico>> ListarOrdensServicoPorCliente(Guid clienteId, CancellationToken ct) => await db.OrdensServico.Where(x => x.ClienteId == clienteId).OrderByDescending(x => x.DataCriacao).ToListAsync(ct);
    public Task<Orcamento?> ObterOrcamento(Guid id, CancellationToken ct) => db.Orcamentos.Include(x => x.ItensServico).Include(x => x.ItensMaterial).FirstOrDefaultAsync(x => x.Id == id, ct);
    public Task<Orcamento?> ObterOrcamentoPorOs(Guid ordemServicoId, CancellationToken ct) => db.Orcamentos.Include(x => x.ItensServico).Include(x => x.ItensMaterial).FirstOrDefaultAsync(x => x.OrdemServicoId == ordemServicoId, ct);
    public Task<Orcamento?> ObterOrcamentoPorTokenAcaoExterna(string token, CancellationToken ct) => db.Orcamentos.Include(x => x.ItensServico).Include(x => x.ItensMaterial).FirstOrDefaultAsync(x => x.TokenAcaoExterna == token, ct);
    public void AdicionarOrdemServico(OrdemServico ordem) => db.OrdensServico.Add(ordem);
    public void AdicionarOrcamento(Orcamento orcamento) => db.Orcamentos.Add(orcamento);
    public Task Salvar(CancellationToken ct) => db.SaveChangesAsync(ct);
}

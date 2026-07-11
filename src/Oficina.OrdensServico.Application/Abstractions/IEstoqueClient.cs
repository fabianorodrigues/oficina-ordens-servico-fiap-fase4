using Oficina.OrdensServico.Application.Contracts;
using Oficina.OrdensServico.Domain.Oficina;

namespace Oficina.OrdensServico.Application.Abstractions;

public interface IEstoqueClient
{
    Task<IReadOnlyList<MaterialDto>> ConsultarMateriais(IReadOnlyCollection<(TipoMaterial Tipo, Guid Id)> materiais, CancellationToken ct);
    Task<DisponibilidadeEstoqueResponse> ConsultarDisponibilidade(DisponibilidadeEstoqueRequest request, CancellationToken ct);
}

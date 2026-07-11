namespace Oficina.OrdensServico.Application.Contracts;

public sealed record OrcamentoDetalheResponse(Guid Id, Guid OrdemServicoId, string Status, decimal ValorTotal, DateTimeOffset DataCriacao, IReadOnlyList<ItemServicoDetalheResponse> ItensServico, IReadOnlyList<ItemMaterialDetalheResponse> ItensMaterial);

using Oficina.OrdensServico.Domain.Oficina;

namespace Oficina.OrdensServico.Application.Contracts;

public sealed record ClienteDto(Guid Id, string Nome, string Documento, string Email, string Telefone);
public sealed record VeiculoDto(Guid Id, Guid ClienteId, string Placa, string Renavam, string ModeloDescricao, string ModeloMarca, int ModeloAno);
public sealed record ServicoDto(Guid Id, string Descricao, decimal MaoDeObra, IReadOnlyList<ReceitaMaterialDto> Pecas, IReadOnlyList<ReceitaMaterialDto> Insumos);
public sealed record ReceitaMaterialDto(Guid Id, int Quantidade);
public sealed record MaterialDto(Guid Id, string Descricao, decimal PrecoUnitario, TipoMaterial Tipo);
public sealed record ConsultaServicosRequest(IReadOnlyList<Guid> Ids);
public sealed record ConsultaServicosResponse(IReadOnlyList<ServicoDto> ServicosEncontrados, IReadOnlyList<Guid> IdsAusentes);
public sealed record CadastroConsultaServicosResponse(IReadOnlyList<CadastroServicoDto> Encontrados, IReadOnlyList<Guid> Ausentes);
public sealed record CadastroServicoDto(Guid Id, decimal MaoDeObra, IReadOnlyList<CadastroReferenciaMaterialDto> Pecas, IReadOnlyList<CadastroReferenciaMaterialDto> Insumos);
public sealed record CadastroReferenciaMaterialDto(Guid ReferenciaId, int Quantidade);
public sealed record DisponibilidadeEstoqueRequest(IReadOnlyList<DisponibilidadeItemRequest> Items);
public sealed record DisponibilidadeItemRequest(TipoMaterial TipoMaterial, Guid MaterialId, int RequestedQuantity);
public sealed record DisponibilidadeEstoqueResponse(bool Informational);

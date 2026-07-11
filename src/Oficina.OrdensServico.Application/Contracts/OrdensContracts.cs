namespace Oficina.OrdensServico.Application.Contracts;

public sealed class AbrirOrdemServicoRequest
{
    public string? TipoManutencao { get; init; }
    public ClienteAberturaRequest Cliente { get; init; } = new();
    public VeiculoAberturaRequest Veiculo { get; init; } = new();
    public ItensAberturaRequest Itens { get; init; } = new();
}
public sealed class ClienteAberturaRequest { public string Nome { get; init; } = string.Empty; public string Documento { get; init; } = string.Empty; public string Email { get; init; } = string.Empty; public string Telefone { get; init; } = string.Empty; }
public sealed class VeiculoAberturaRequest { public string Placa { get; init; } = string.Empty; public string Renavam { get; init; } = string.Empty; public ModeloAberturaRequest Modelo { get; init; } = new(); }
public sealed class ModeloAberturaRequest { public string Descricao { get; init; } = string.Empty; public string Marca { get; init; } = string.Empty; public int Ano { get; init; } }
public sealed class ItensAberturaRequest { public IReadOnlyList<ServicoAberturaRequest> Servicos { get; init; } = []; public IReadOnlyList<PecaAberturaRequest> Pecas { get; init; } = []; public IReadOnlyList<InsumoAberturaRequest> Insumos { get; init; } = []; }
public sealed class ServicoAberturaRequest { public Guid ServicoId { get; init; } }
public sealed class PecaAberturaRequest { public Guid PecaId { get; init; } public int Quantidade { get; init; } }
public sealed class InsumoAberturaRequest { public Guid InsumoId { get; init; } public int Quantidade { get; init; } }
public sealed class ClassificarOrdemServicoRequest { public string TipoManutencao { get; init; } = string.Empty; }
public sealed record RegistrarDiagnosticoRequest(string Descricao, IReadOnlyList<Guid> ServicoIds);
public sealed record AbrirOrdemServicoResponse { public Guid Id { get; init; } public string Status { get; init; } = string.Empty; public decimal Total { get; init; } }
public sealed record RegistrarDiagnosticoResponse { public Guid OrcamentoId { get; init; } }
public sealed record StatusOrdemServicoResponse(Guid Id, string Status, string TipoManutencao, DateTimeOffset DataUltimaAtualizacao);
public sealed record OrdemServicoListaItemResponse(Guid Id, Guid VeiculoId, string TipoManutencao, string Status, DateTimeOffset DataCriacao);
public sealed record DiagnosticoDetalheResponse(string Descricao, DateTimeOffset DataRegistro);
public sealed record ItemServicoDetalheResponse(Guid ServicoId, decimal ValorMaoDeObra, string? Descricao);
public sealed record ItemMaterialDetalheResponse(string Tipo, Guid MaterialId, int Quantidade, decimal ValorUnitario, string? Descricao);
public sealed record OrdemServicoDetalheResponse(Guid Id, Guid VeiculoId, string TipoManutencao, string Status, string OrigemUltimaAtualizacaoStatus, DateTimeOffset DataUltimaAtualizacaoStatus, DateTimeOffset DataCriacao, DateTimeOffset? DataInicioExecucao, DateTimeOffset? DataFimExecucao, DiagnosticoDetalheResponse? Diagnostico, OrcamentoDetalheResponse? Orcamento);

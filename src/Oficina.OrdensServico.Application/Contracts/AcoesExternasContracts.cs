namespace Oficina.OrdensServico.Application.Contracts;

public enum AcaoExternaOrcamento { Aprovar = 1, Recusar = 2 }
public sealed class ProcessarAcaoExternaOrcamentoRequest { public string Token { get; init; } = string.Empty; public AcaoExternaOrcamento Acao { get; init; } }
public sealed class ProcessarAcaoExternaOrcamentoResponse { public bool Sucesso { get; init; } public string Codigo { get; init; } = string.Empty; public string Mensagem { get; init; } = string.Empty; public Guid? OrcamentoId { get; init; } public Guid? OrdemServicoId { get; init; } }

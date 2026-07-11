using Oficina.OrdensServico.Domain.Shared;

namespace Oficina.OrdensServico.Domain.Oficina;

public sealed class Orcamento : Entidade
{
    private readonly List<OrcamentoItemServico> _itensServico = [];
    private readonly List<OrcamentoItemMaterial> _itensMaterial = [];
    private Orcamento() { }

    public Orcamento(Guid ordemServicoId, decimal valorTotal)
    {
        if (ordemServicoId == Guid.Empty) throw new ArgumentException("Ordem de servico invalida.");
        OrdemServicoId = ordemServicoId;
        ValorTotal = valorTotal;
        Status = StatusOrcamento.AguardandoAprovacao;
        DataCriacao = DateTimeOffset.UtcNow;
    }

    public Guid OrdemServicoId { get; private set; }
    public StatusOrcamento Status { get; private set; }
    public decimal ValorTotal { get; private set; }
    public DateTimeOffset DataCriacao { get; private set; }
    public string? TokenAcaoExterna { get; private set; }
    public DateTimeOffset? TokenAcaoExternaExpiraEm { get; private set; }
    public IReadOnlyCollection<OrcamentoItemServico> ItensServico => _itensServico;
    public IReadOnlyCollection<OrcamentoItemMaterial> ItensMaterial => _itensMaterial;

    public void DefinirItensServico(IEnumerable<OrcamentoItemServico> itens) { _itensServico.Clear(); _itensServico.AddRange(itens); }
    public void DefinirItensMaterial(IEnumerable<OrcamentoItemMaterial> itens) { _itensMaterial.Clear(); _itensMaterial.AddRange(itens); }
    public void DefinirTokenAcaoExterna(string token, DateTimeOffset expiraEm) { TokenAcaoExterna = string.IsNullOrWhiteSpace(token) ? throw new ArgumentException("Token invalido.") : token; TokenAcaoExternaExpiraEm = expiraEm; }
    public void Aprovar()
    {
        if (Status != StatusOrcamento.AguardandoAprovacao) throw new InvalidOperationException("Orcamento nao esta aguardando aprovacao.");
        Status = StatusOrcamento.Aprovado;
    }
    public void Recusar()
    {
        if (Status != StatusOrcamento.AguardandoAprovacao) throw new InvalidOperationException("Orcamento nao esta aguardando aprovacao.");
        Status = StatusOrcamento.Recusado;
    }
}

public sealed class OrcamentoItemServico : Entidade
{
    private OrcamentoItemServico() { }
    public OrcamentoItemServico(Guid servicoId, decimal valorMaoDeObra, string descricao)
    {
        if (servicoId == Guid.Empty) throw new ArgumentException("Servico invalido.");
        if (valorMaoDeObra < 0) throw new ArgumentOutOfRangeException(nameof(valorMaoDeObra));
        ServicoId = servicoId; ValorMaoDeObra = valorMaoDeObra; DescricaoSnapshot = descricao;
    }
    public Guid ServicoId { get; private set; }
    public decimal ValorMaoDeObra { get; private set; }
    public string DescricaoSnapshot { get; private set; } = string.Empty;
}

public sealed class OrcamentoItemMaterial : Entidade
{
    private OrcamentoItemMaterial() { }
    public OrcamentoItemMaterial(TipoMaterial tipo, Guid materialId, int quantidade, decimal valorUnitario, string descricao)
    {
        if (materialId == Guid.Empty) throw new ArgumentException("Material invalido.");
        if (quantidade <= 0) throw new ArgumentOutOfRangeException(nameof(quantidade));
        if (valorUnitario < 0) throw new ArgumentOutOfRangeException(nameof(valorUnitario));
        Tipo = tipo; MaterialId = materialId; Quantidade = quantidade; ValorUnitario = valorUnitario; DescricaoSnapshot = descricao; ValorTotal = quantidade * valorUnitario;
    }
    public TipoMaterial Tipo { get; private set; }
    public Guid MaterialId { get; private set; }
    public int Quantidade { get; private set; }
    public decimal ValorUnitario { get; private set; }
    public decimal ValorTotal { get; private set; }
    public string DescricaoSnapshot { get; private set; } = string.Empty;
}

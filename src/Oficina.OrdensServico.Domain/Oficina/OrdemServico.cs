using Oficina.OrdensServico.Domain.Shared;

namespace Oficina.OrdensServico.Domain.Oficina;

public sealed class OrdemServico : Entidade
{
    private readonly List<ItemServicoOs> _itensServico = [];
    private OrdemServico() { }

    private OrdemServico(Guid clienteId, Guid veiculoId, SnapshotCliente cliente, SnapshotVeiculo veiculo)
    {
        if (clienteId == Guid.Empty || veiculoId == Guid.Empty) throw new ArgumentException("Cliente e veiculo sao obrigatorios.");
        ClienteId = clienteId;
        VeiculoId = veiculoId;
        ClienteSnapshot = cliente;
        VeiculoSnapshot = veiculo;
        TipoManutencao = TipoManutencao.NaoClassificada;
        DataCriacao = DateTimeOffset.UtcNow;
        AtualizarStatus(StatusOrdemServico.Recebida, OrigemAtualizacaoStatusOs.Interna);
    }

    public Guid ClienteId { get; private set; }
    public Guid VeiculoId { get; private set; }
    public TipoManutencao TipoManutencao { get; private set; }
    public StatusOrdemServico Status { get; private set; }
    public OrigemAtualizacaoStatusOs OrigemUltimaAtualizacaoStatus { get; private set; }
    public DateTimeOffset DataUltimaAtualizacaoStatus { get; private set; }
    public DateTimeOffset DataCriacao { get; private set; }
    public DateTimeOffset? DataInicioExecucao { get; private set; }
    public DateTimeOffset? DataFimExecucao { get; private set; }
    public Guid? OrcamentoId { get; private set; }
    public Diagnostico? Diagnostico { get; private set; }
    public SnapshotCliente ClienteSnapshot { get; private set; } = null!;
    public SnapshotVeiculo VeiculoSnapshot { get; private set; } = null!;
    public IReadOnlyCollection<ItemServicoOs> ItensServico => _itensServico;

    public static OrdemServico CriarRecebida(Guid clienteId, Guid veiculoId, SnapshotCliente cliente, SnapshotVeiculo veiculo) => new(clienteId, veiculoId, cliente, veiculo);

    public void Classificar(TipoManutencao tipo, IEnumerable<Guid>? servicoIds = null, OrigemAtualizacaoStatusOs origem = OrigemAtualizacaoStatusOs.Interna)
    {
        if (Status != StatusOrdemServico.Recebida) throw new InvalidOperationException("Somente OS recebida pode ser classificada.");
        if (TipoManutencao != TipoManutencao.NaoClassificada) throw new InvalidOperationException("OS ja classificada.");
        if (tipo == TipoManutencao.NaoClassificada) throw new InvalidOperationException("Tipo de manutencao invalido para classificacao.");
        TipoManutencao = tipo;
        if (tipo == TipoManutencao.Preventiva)
        {
            var lista = servicoIds?.Where(x => x != Guid.Empty).Distinct().ToList() ?? [];
            if (lista.Count == 0) throw new ArgumentException("OS preventiva exige ao menos 1 servico.");
            _itensServico.Clear();
            foreach (var id in lista) _itensServico.Add(new ItemServicoOs(id));
            AtualizarStatus(StatusOrdemServico.AguardandoAprovacao, origem);
        }
        else
        {
            AtualizarStatus(StatusOrdemServico.EmDiagnostico, origem);
        }
    }

    public void RegistrarDiagnostico(string descricao, IEnumerable<Guid> servicoIds)
    {
        if (TipoManutencao != TipoManutencao.Corretiva) throw new InvalidOperationException("Diagnostico so existe em OS corretiva.");
        if (Status is not StatusOrdemServico.Recebida and not StatusOrdemServico.EmDiagnostico) throw new InvalidOperationException("OS nao esta em diagnostico.");
        var lista = servicoIds.Where(x => x != Guid.Empty).Distinct().ToList();
        if (lista.Count == 0) throw new ArgumentException("Diagnostico exige ao menos 1 servico identificado.");
        Diagnostico = new Diagnostico(descricao);
        AtualizarStatus(StatusOrdemServico.AguardandoAprovacao, OrigemAtualizacaoStatusOs.Interna);
    }

    public void VincularOrcamento(Guid orcamentoId, bool atualizarStatusParaAguardando = true)
    {
        if (orcamentoId == Guid.Empty) throw new ArgumentException("Orcamento invalido.");
        OrcamentoId = orcamentoId;
        if (atualizarStatusParaAguardando && Status == StatusOrdemServico.Recebida) AtualizarStatus(StatusOrdemServico.AguardandoAprovacao, OrigemAtualizacaoStatusOs.Interna);
    }

    public void Finalizar()
    {
        if (Status != StatusOrdemServico.EmExecucao) throw new InvalidOperationException("OS so pode ser finalizada apos execucao.");
        AtualizarStatus(StatusOrdemServico.Finalizada, OrigemAtualizacaoStatusOs.Interna);
        DataFimExecucao = DateTimeOffset.UtcNow;
    }

    public void IniciarExecucao()
    {
        if (Status != StatusOrdemServico.AguardandoAprovacao) throw new InvalidOperationException("OS so pode iniciar execucao apos aprovacao e reserva.");
        AtualizarStatus(StatusOrdemServico.EmExecucao, OrigemAtualizacaoStatusOs.Interna);
        DataInicioExecucao ??= DateTimeOffset.UtcNow;
    }

    public void RetornarParaEsperaAposCompensacao()
    {
        if (Status is not StatusOrdemServico.EmExecucao and not StatusOrdemServico.AguardandoAprovacao) throw new InvalidOperationException("OS nao pode ser compensada neste status.");
        AtualizarStatus(StatusOrdemServico.AguardandoAprovacao, OrigemAtualizacaoStatusOs.Interna);
        DataInicioExecucao = null;
    }

    public void MarcarEntregue()
    {
        if (Status != StatusOrdemServico.Finalizada) throw new InvalidOperationException("OS so pode ser entregue apos finalizacao.");
        AtualizarStatus(StatusOrdemServico.Entregue, OrigemAtualizacaoStatusOs.Interna);
    }

    public void FinalizarPorRecusa(Orcamento orcamento)
    {
        if (orcamento.Status != StatusOrcamento.Recusado) throw new InvalidOperationException("Orcamento precisa estar recusado.");
        AtualizarStatus(StatusOrdemServico.Finalizada, OrigemAtualizacaoStatusOs.Interna);
    }

    private void AtualizarStatus(StatusOrdemServico status, OrigemAtualizacaoStatusOs origem)
    {
        Status = status;
        OrigemUltimaAtualizacaoStatus = origem;
        DataUltimaAtualizacaoStatus = DateTimeOffset.UtcNow;
    }
}

public sealed class ItemServicoOs : Entidade
{
    private ItemServicoOs() { }
    public ItemServicoOs(Guid servicoId)
    {
        if (servicoId == Guid.Empty) throw new ArgumentException("Servico invalido.");
        ServicoId = servicoId;
    }
    public Guid ServicoId { get; private set; }
}

public sealed record SnapshotCliente(Guid ClienteId, string Nome, string Documento, string Email, string Telefone);
public sealed record SnapshotVeiculo(Guid VeiculoId, string Placa, string Renavam, string ModeloDescricao, string Marca, int Ano);
public sealed record Diagnostico(string Descricao)
{
    public DateTimeOffset DataRegistro { get; init; } = DateTimeOffset.UtcNow;
}

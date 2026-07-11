using FluentValidation;
using Microsoft.Extensions.Options;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Application.Contracts;
using Oficina.OrdensServico.Application.Shared;
using Oficina.OrdensServico.Domain.Oficina;

namespace Oficina.OrdensServico.Application.UseCases;

public sealed class OrdensUseCases(IOrdensServicoRepository repo, ICadastroClient cadastro, IEstoqueClient estoque, IValidator<AbrirOrdemServicoRequest> abrirValidator, IValidator<RegistrarDiagnosticoRequest> diagnosticoValidator, INotificadorCliente notificador, IOptions<DistributedFlowOptions> distributed, IFluxoDistribuidoOrdens fluxoDistribuido)
{
    private static readonly TimeSpan PrazoExpiracaoAcaoExterna = TimeSpan.FromDays(7);

    public async Task<AbrirOrdemServicoResponse> Abrir(AbrirOrdemServicoRequest req, CancellationToken ct)
    {
        await abrirValidator.ValidateAndThrowAsync(req, ct);
        var cliente = await cadastro.ObterClientePorDocumento(req.Cliente.Documento, ct) ?? throw new OrdensException("Cliente nao encontrado.", 404, "cliente_inexistente");
        var veiculo = await cadastro.ObterVeiculoPorPlaca(req.Veiculo.Placa, ct) ?? throw new OrdensException("Veiculo nao encontrado.", 404, "veiculo_inexistente");
        if (veiculo.ClienteId != cliente.Id) throw new OrdensException("Veiculo nao pertence ao cliente.", 403, "ownership");

        var servicoIds = req.Itens.Servicos.Select(x => x.ServicoId).Where(x => x != Guid.Empty).Distinct().ToList();
        var tipo = ParseTipo(req.TipoManutencao);
        var os = OrdemServico.CriarRecebida(cliente.Id, veiculo.Id, Snap(cliente), Snap(veiculo));
        if (tipo != TipoManutencao.NaoClassificada) os.Classificar(tipo, servicoIds);

        decimal total = 0;
        if (tipo == TipoManutencao.Preventiva || (tipo == TipoManutencao.NaoClassificada && servicoIds.Count > 0))
        {
            var orcamento = await CriarOrcamento(os.Id, servicoIds, req.Itens.Pecas.Select(x => (TipoMaterial.Peca, x.PecaId, x.Quantidade)).Concat(req.Itens.Insumos.Select(x => (TipoMaterial.Insumo, x.InsumoId, x.Quantidade))).ToList(), ct);
            total = orcamento.ValorTotal;
            orcamento.DefinirTokenAcaoExterna(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.Add(PrazoExpiracaoAcaoExterna));
            os.VincularOrcamento(orcamento.Id, atualizarStatusParaAguardando: false);
            repo.AdicionarOrcamento(orcamento);
        }

        repo.AdicionarOrdemServico(os);
        await repo.Salvar(ct);
        return new AbrirOrdemServicoResponse { Id = os.Id, Status = os.Status.ToString(), Total = total };
    }

    public async Task Classificar(Guid id, string tipoTexto, CancellationToken ct)
    {
        var os = await ObterOs(id, ct);
        os.Classificar(ParseTipo(tipoTexto));
        await repo.Salvar(ct);
    }

    public async Task<RegistrarDiagnosticoResponse> RegistrarDiagnostico(Guid id, RegistrarDiagnosticoRequest req, CancellationToken ct)
    {
        await diagnosticoValidator.ValidateAndThrowAsync(req, ct);
        var os = await ObterOs(id, ct);
        if (await repo.ObterOrcamentoPorOs(id, ct) is not null) throw new OrdensException("Ordem de servico ja possui orcamento.", 409);
        os.RegistrarDiagnostico(req.Descricao, req.ServicoIds);
        var orc = await CriarOrcamento(os.Id, req.ServicoIds.Distinct().ToList(), [], ct);
        orc.DefinirTokenAcaoExterna(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.Add(PrazoExpiracaoAcaoExterna));
        os.VincularOrcamento(orc.Id);
        repo.AdicionarOrcamento(orc);
        await repo.Salvar(ct);
        await notificador.NotificarOrcamentoCriado(orc.Id, os.Id, ct);
        return new RegistrarDiagnosticoResponse { OrcamentoId = orc.Id };
    }

    public async Task<StatusOrdemServicoResponse> Status(Guid id, CancellationToken ct)
    {
        var os = await ObterOs(id, ct);
        return new(os.Id, os.Status.ToString(), os.TipoManutencao.ToString(), os.DataUltimaAtualizacaoStatus);
    }

    public async Task<OrdemServicoDetalheResponse> Obter(Guid id, CancellationToken ct) => Map(await ObterOs(id, ct), await repo.ObterOrcamentoPorOs(id, ct));
    public async Task<IReadOnlyList<OrdemServicoListaItemResponse>> Listar(CancellationToken ct) => (await repo.ListarOrdensServico(ct)).Select(MapLista).ToList();
    public async Task<IReadOnlyList<OrdemServicoListaItemResponse>> ListarMinhas(Guid clienteId, CancellationToken ct) => (await repo.ListarOrdensServicoPorCliente(clienteId, ct)).Select(MapLista).ToList();
    public async Task Finalizar(Guid id, CancellationToken ct) { var os = await ObterOs(id, ct); os.Finalizar(); await repo.Salvar(ct); }
    public async Task Entregar(Guid id, CancellationToken ct) { var os = await ObterOs(id, ct); os.MarcarEntregue(); await repo.Salvar(ct); }

    public async Task<OrcamentoDetalheResponse> ObterOrcamento(Guid id, CancellationToken ct) => MapOrc(await ObterOrc(id, ct));

    public async Task AprovarOrcamento(Guid id, CancellationToken ct)
    {
        var orc = await ObterOrc(id, ct);
        if (!distributed.Value.Enabled) throw new OrdensException("Aprovacao distribuida indisponivel enquanto DistributedFlow__Enabled=false.", 423, "distributed_flow_disabled");
        if (orc.Status == StatusOrcamento.Aprovado)
        {
            await fluxoDistribuido.IniciarAprovacao(orc, ct);
            await repo.Salvar(ct);
            return;
        }

        try { orc.Aprovar(); }
        catch (InvalidOperationException ex) { throw new OrdensException(ex.Message, 409); }
        await fluxoDistribuido.IniciarAprovacao(orc, ct);
        await repo.Salvar(ct);
    }

    public async Task RecusarOrcamento(Guid id, CancellationToken ct)
    {
        var orc = await ObterOrc(id, ct);
        var os = await ObterOs(orc.OrdemServicoId, ct);
        orc.Recusar();
        os.FinalizarPorRecusa(orc);
        await repo.Salvar(ct);
        await notificador.NotificarOrcamentoRecusado(orc.Id, os.Id, ct);
    }

    public async Task<ProcessarAcaoExternaOrcamentoResponse> ProcessarAcaoExterna(ProcessarAcaoExternaOrcamentoRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) return new() { Sucesso = false, Codigo = "token_invalido", Mensagem = "Token invalido." };
        var orc = await repo.ObterOrcamentoPorTokenAcaoExterna(request.Token, ct);
        if (orc is null) return new() { Sucesso = false, Codigo = "link_invalido", Mensagem = "Link invalido." };
        if (orc.TokenAcaoExternaExpiraEm < DateTimeOffset.UtcNow) return new() { Sucesso = false, Codigo = "link_expirado", Mensagem = "Link expirado.", OrcamentoId = orc.Id, OrdemServicoId = orc.OrdemServicoId };
        if (orc.Status != StatusOrcamento.AguardandoAprovacao) return new() { Sucesso = false, Codigo = "acao_ja_processada", Mensagem = "Acao ja processada.", OrcamentoId = orc.Id, OrdemServicoId = orc.OrdemServicoId };
        if (request.Acao == AcaoExternaOrcamento.Aprovar) await AprovarOrcamento(orc.Id, ct); else await RecusarOrcamento(orc.Id, ct);
        return new() { Sucesso = true, Codigo = "sucesso", Mensagem = "Acao processada.", OrcamentoId = orc.Id, OrdemServicoId = orc.OrdemServicoId };
    }

    public async Task<RelatorioTempoMedioExecucaoResponse> Relatorio(CancellationToken ct)
    {
        var finalizadas = (await repo.ListarOrdensServico(ct)).Where(x => x.DataInicioExecucao is not null && x.DataFimExecucao is not null).ToList();
        return finalizadas.Count == 0 ? new(0, 0) : new(finalizadas.Average(x => (x.DataFimExecucao!.Value - x.DataInicioExecucao!.Value).TotalHours), finalizadas.Count);
    }

    private async Task<Orcamento> CriarOrcamento(Guid osId, IReadOnlyList<Guid> servicoIds, IReadOnlyList<(TipoMaterial Tipo, Guid Id, int Quantidade)> extras, CancellationToken ct)
    {
        var servicos = await cadastro.ConsultarServicos(new ConsultaServicosRequest(servicoIds.Distinct().ToList()), ct);
        if (servicos.IdsAusentes.Count > 0) throw new OrdensException("Servico nao encontrado.", 404);
        var itensServico = servicos.ServicosEncontrados.Select(s => new OrcamentoItemServico(s.Id, s.MaoDeObra, s.Descricao)).ToList();
        var materiais = new List<(TipoMaterial Tipo, Guid Id, int Quantidade)>();
        materiais.AddRange(servicos.ServicosEncontrados.SelectMany(s => s.Pecas.Select(p => (TipoMaterial.Peca, p.Id, p.Quantidade)).Concat(s.Insumos.Select(i => (TipoMaterial.Insumo, i.Id, i.Quantidade)))));
        materiais.AddRange(extras);
        var agregados = materiais.GroupBy(x => new { x.Tipo, x.Id }).Select(g => (g.Key.Tipo, g.Key.Id, Quantidade: g.Sum(x => x.Quantidade))).ToList();
        var detalhes = await estoque.ConsultarMateriais(agregados.Select(x => (x.Tipo, x.Id)).ToList(), ct);
        await estoque.ConsultarDisponibilidade(new DisponibilidadeEstoqueRequest(agregados.Select(x => new DisponibilidadeItemRequest(x.Tipo, x.Id, x.Quantidade)).ToList()), ct);
        var itensMaterial = agregados.Select(a =>
        {
            var mat = detalhes.FirstOrDefault(m => m.Tipo == a.Tipo && m.Id == a.Id) ?? throw new OrdensException("Material nao encontrado.", 404);
            return new OrcamentoItemMaterial(a.Tipo, a.Id, a.Quantidade, mat.PrecoUnitario, mat.Descricao);
        }).ToList();
        var total = itensServico.Sum(x => x.ValorMaoDeObra) + itensMaterial.Sum(x => x.ValorTotal);
        var orc = new Orcamento(osId, total);
        orc.DefinirItensServico(itensServico);
        orc.DefinirItensMaterial(itensMaterial);
        return orc;
    }

    private async Task<OrdemServico> ObterOs(Guid id, CancellationToken ct) => await repo.ObterOrdemServico(id, ct) ?? throw new OrdensException("Ordem de servico nao encontrada.", 404);
    private async Task<Orcamento> ObterOrc(Guid id, CancellationToken ct) => await repo.ObterOrcamento(id, ct) ?? throw new OrdensException("Orcamento nao encontrado.", 404);
    private static TipoManutencao ParseTipo(string? tipo) => string.IsNullOrWhiteSpace(tipo) ? TipoManutencao.NaoClassificada : Enum.TryParse<TipoManutencao>(tipo, true, out var v) ? v : throw new OrdensException("Tipo de manutencao invalido.", 400);
    private static SnapshotCliente Snap(ClienteDto c) => new(c.Id, c.Nome, c.Documento, c.Email, c.Telefone);
    private static SnapshotVeiculo Snap(VeiculoDto v) => new(v.Id, v.Placa, v.Renavam, v.ModeloDescricao, v.ModeloMarca, v.ModeloAno);
    private static OrdemServicoListaItemResponse MapLista(OrdemServico x) => new(x.Id, x.VeiculoId, x.TipoManutencao.ToString(), x.Status.ToString(), x.DataCriacao);
    private static OrdemServicoDetalheResponse Map(OrdemServico os, Orcamento? orc) => new(os.Id, os.VeiculoId, os.TipoManutencao.ToString(), os.Status.ToString(), os.OrigemUltimaAtualizacaoStatus.ToString(), os.DataUltimaAtualizacaoStatus, os.DataCriacao, os.DataInicioExecucao, os.DataFimExecucao, os.Diagnostico is null ? null : new(os.Diagnostico.Descricao, os.Diagnostico.DataRegistro), orc is null ? null : MapOrc(orc));
    private static OrcamentoDetalheResponse MapOrc(Orcamento o) => new(o.Id, o.OrdemServicoId, o.Status.ToString(), o.ValorTotal, o.DataCriacao, o.ItensServico.Select(i => new ItemServicoDetalheResponse(i.ServicoId, i.ValorMaoDeObra, i.DescricaoSnapshot)).ToList(), o.ItensMaterial.Select(i => new ItemMaterialDetalheResponse(i.Tipo.ToString(), i.MaterialId, i.Quantidade, i.ValorUnitario, i.DescricaoSnapshot)).ToList());
}

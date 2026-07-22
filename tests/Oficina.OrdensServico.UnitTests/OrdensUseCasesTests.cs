using Microsoft.Extensions.Options;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Application.Contracts;
using Oficina.OrdensServico.Application.Shared;
using Oficina.OrdensServico.Application.UseCases;
using Oficina.OrdensServico.Application.Validators;
using Oficina.OrdensServico.Domain.Oficina;

namespace Oficina.OrdensServico.UnitTests;

public sealed class OrdensUseCasesTests
{
    [Fact]
    public async Task Cliente_inexistente_nao_persiste_ordem()
    {
        var repo = new FakeRepo();
        var cadastro = new FakeCadastro { Cliente = null };
        var use = Criar(repo, cadastro, new FakeEstoque());
        await Assert.ThrowsAsync<OrdensException>(() => use.Abrir(Request(), CancellationToken.None));
        Assert.Empty(repo.Ordens);
    }

    [Fact]
    public async Task Veiculo_inexistente_nao_persiste_ordem()
    {
        var repo = new FakeRepo();
        var cadastro = new FakeCadastro { Veiculo = null };
        var use = Criar(repo, cadastro, new FakeEstoque());
        await Assert.ThrowsAsync<OrdensException>(() => use.Abrir(Request(), CancellationToken.None));
        Assert.Empty(repo.Ordens);
    }

    [Fact]
    public async Task Ownership_invalido_bloqueia_abertura()
    {
        var repo = new FakeRepo();
        var cadastro = new FakeCadastro { Veiculo = new(Guid.NewGuid(), Guid.NewGuid(), "ABC1D23", "123", "Modelo", "Marca", 2024) };
        var use = Criar(repo, cadastro, new FakeEstoque());
        await Assert.ThrowsAsync<OrdensException>(() => use.Abrir(Request(), CancellationToken.None));
        Assert.Empty(repo.Ordens);
    }

    [Fact]
    public async Task Falha_http_de_integracao_nao_deixa_registro_parcial()
    {
        var repo = new FakeRepo();
        var use = Criar(repo, new FakeCadastro(), new FakeEstoque { Falhar = true });
        await Assert.ThrowsAsync<HttpRequestException>(() => use.Abrir(Request(), CancellationToken.None));
        Assert.Empty(repo.Ordens);
    }

    [Fact]
    public async Task Calcula_orcamento_deduplica_servicos_expande_receitas_e_agrega_materiais()
    {
        var repo = new FakeRepo();
        var use = Criar(repo, new FakeCadastro(), new FakeEstoque());

        var result = await use.Abrir(Request(), CancellationToken.None);

        var orcamento = repo.Orcamentos.Single();
        Assert.Equal(250m, result.Total);
        Assert.Single(orcamento.ItensServico);
        Assert.Equal(1, orcamento.ItensMaterial.Count(x => x.Tipo == TipoMaterial.Peca));
        Assert.Equal(3, orcamento.ItensMaterial.Single(x => x.Tipo == TipoMaterial.Peca).Quantidade);
        Assert.Equal("Filtro snapshot", orcamento.ItensMaterial.Single(x => x.Tipo == TipoMaterial.Peca).DescricaoSnapshot);
    }

    [Fact]
    public async Task Cliente_nao_acessa_ordem_de_outro_cliente()
    {
        var repo = new FakeRepo();
        var cadastro = new FakeCadastro();
        var use = Criar(repo, cadastro, new FakeEstoque());
        var result = await use.Abrir(Request(), CancellationToken.None);

        var proprio = await use.ObterDoCliente(result.Id, cadastro.ClienteId, CancellationToken.None);
        Assert.Equal(result.Id, proprio.Id);

        var ex = await Assert.ThrowsAsync<OrdensException>(
            () => use.ObterDoCliente(result.Id, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Cliente_nao_acessa_orcamento_de_outro_cliente()
    {
        var repo = new FakeRepo();
        var cadastro = new FakeCadastro();
        var use = Criar(repo, cadastro, new FakeEstoque());
        await use.Abrir(Request(), CancellationToken.None);
        var orcamentoId = repo.Orcamentos.Single().Id;

        var proprio = await use.ObterOrcamentoDoCliente(orcamentoId, cadastro.ClienteId, CancellationToken.None);
        Assert.Equal(orcamentoId, proprio.Id);

        var ex = await Assert.ThrowsAsync<OrdensException>(
            () => use.ObterOrcamentoDoCliente(orcamentoId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Snapshots_locais_nao_mudam_apos_alteracao_remota()
    {
        var repo = new FakeRepo();
        var cadastro = new FakeCadastro();
        var estoque = new FakeEstoque();
        var use = Criar(repo, cadastro, estoque);

        var result = await use.Abrir(Request(), CancellationToken.None);
        cadastro.Cliente = cadastro.Cliente! with { Nome = "Nome alterado" };
        estoque.MaterialDescricao = "Descricao remota alterada";
        var detalhe = await use.Obter(result.Id, CancellationToken.None);

        Assert.Equal("Cliente", repo.Ordens.Single().ClienteSnapshot.Nome);
        Assert.Equal("Filtro snapshot", detalhe.Orcamento!.ItensMaterial.Single(x => x.Tipo == "Peca").Descricao);
    }

    [Fact]
    public async Task Aprovacao_temporaria_retorna_erro_controlado_e_nao_avanca_execucao()
    {
        var repo = new FakeRepo();
        var use = Criar(repo, new FakeCadastro(), new FakeEstoque());
        await use.Abrir(Request(), CancellationToken.None);
        var orc = repo.Orcamentos.Single();

        var ex = await Assert.ThrowsAsync<OrdensException>(() => use.AprovarOrcamento(orc.Id, CancellationToken.None));

        Assert.Equal("distributed_flow_disabled", ex.Code);
        Assert.Equal(StatusOrcamento.AguardandoAprovacao, orc.Status);
        Assert.NotEqual(StatusOrdemServico.EmExecucao, repo.Ordens.Single().Status);
    }

    [Fact]
    public async Task Aprovacao_distribuida_aprova_orcamento_e_inicia_fluxo_idempotente()
    {
        var repo = new FakeRepo();
        var fluxo = new FakeFluxoDistribuido();
        var use = Criar(repo, new FakeCadastro(), new FakeEstoque(), fluxo: fluxo, distributedEnabled: true);
        await use.Abrir(Request(), CancellationToken.None);
        var orc = repo.Orcamentos.Single();

        await use.AprovarOrcamento(orc.Id, CancellationToken.None);
        await use.AprovarOrcamento(orc.Id, CancellationToken.None);

        Assert.Equal(StatusOrcamento.Aprovado, orc.Status);
        Assert.Equal(2, fluxo.Inicios);
        Assert.Equal(StatusOrdemServico.AguardandoAprovacao, repo.Ordens.Single().Status);
    }

    [Fact]
    public async Task Recusa_finaliza_ordem_e_notifica()
    {
        var repo = new FakeRepo();
        var notifier = new FakeNotifier();
        var use = Criar(repo, new FakeCadastro(), new FakeEstoque(), notifier);
        await use.Abrir(Request(), CancellationToken.None);

        await use.RecusarOrcamento(repo.Orcamentos.Single().Id, CancellationToken.None);

        Assert.Equal(StatusOrcamento.Recusado, repo.Orcamentos.Single().Status);
        Assert.Equal(StatusOrdemServico.Finalizada, repo.Ordens.Single().Status);
        Assert.True(notifier.Recusado);
    }

    [Fact]
    public async Task Status_listagem_minhas_e_relatorio_retorna_dados_locais()
    {
        var repo = new FakeRepo();
        var cadastro = new FakeCadastro();
        var use = Criar(repo, cadastro, new FakeEstoque());
        var result = await use.Abrir(Request(), CancellationToken.None);

        var status = await use.Status(result.Id, CancellationToken.None);
        var lista = await use.Listar(CancellationToken.None);
        var minhas = await use.ListarMinhas(cadastro.ClienteId, CancellationToken.None);
        var relatorio = await use.Relatorio(CancellationToken.None);

        Assert.Equal("AguardandoAprovacao", status.Status);
        Assert.Single(lista);
        Assert.Single(minhas);
        Assert.Equal(0, relatorio.QuantidadeOrdensFinalizadas);
    }

    [Fact]
    public async Task Diagnostico_cria_orcamento_e_notifica()
    {
        var repo = new FakeRepo();
        var notifier = new FakeNotifier();
        var use = Criar(repo, new FakeCadastro(), new FakeEstoque(), notifier);
        var request = Request();
        var corretiva = new AbrirOrdemServicoRequest
        {
            TipoManutencao = "Corretiva",
            Cliente = request.Cliente,
            Veiculo = request.Veiculo,
            Itens = new()
        };
        var aberta = await use.Abrir(corretiva, CancellationToken.None);

        var response = await use.RegistrarDiagnostico(aberta.Id, new RegistrarDiagnosticoRequest("Barulho", [FakeEstoque.ServicoId]), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, response.OrcamentoId);
        Assert.NotNull(repo.Ordens.Single().Diagnostico);
        Assert.True(notifier.Criado);
    }

    [Fact]
    public async Task Acoes_externas_retornam_codigos_publicos()
    {
        var repo = new FakeRepo();
        var use = Criar(repo, new FakeCadastro(), new FakeEstoque());

        var vazio = await use.ProcessarAcaoExterna(new ProcessarAcaoExternaOrcamentoRequest { Token = "" }, CancellationToken.None);
        var ausente = await use.ProcessarAcaoExterna(new ProcessarAcaoExternaOrcamentoRequest { Token = "x" }, CancellationToken.None);

        await use.Abrir(Request(), CancellationToken.None);
        var token = repo.Orcamentos.Single().TokenAcaoExterna!;
        var bloqueado = await Assert.ThrowsAsync<OrdensException>(() => use.ProcessarAcaoExterna(new ProcessarAcaoExternaOrcamentoRequest { Token = token, Acao = AcaoExternaOrcamento.Aprovar }, CancellationToken.None));

        Assert.Equal("token_invalido", vazio.Codigo);
        Assert.Equal("link_invalido", ausente.Codigo);
        Assert.Equal("distributed_flow_disabled", bloqueado.Code);
    }

    [Fact]
    public void Validators_e_dominio_rejeitam_entradas_invalidas()
    {
        Assert.False(new AbrirOrdemServicoRequestValidator().Validate(new AbrirOrdemServicoRequest()).IsValid);
        Assert.False(new RegistrarDiagnosticoRequestValidator().Validate(new RegistrarDiagnosticoRequest("", [])).IsValid);
        Assert.Throws<ArgumentException>(() => OrdemServico.CriarRecebida(Guid.Empty, Guid.NewGuid(), new(Guid.NewGuid(), "n", "d", "e", "t"), new(Guid.NewGuid(), "p", "r", "m", "ma", 2024)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrcamentoItemMaterial(TipoMaterial.Peca, Guid.NewGuid(), 0, 1, "p"));
    }

    private static AbrirOrdemServicoRequest Request() => new()
    {
        TipoManutencao = "Preventiva",
        Cliente = new() { Nome = "Cliente", Documento = "12345678901", Email = "c@x.com", Telefone = "11" },
        Veiculo = new() { Placa = "ABC1D23", Renavam = "123", Modelo = new() { Descricao = "Modelo", Marca = "Marca", Ano = 2024 } },
        Itens = new() { Servicos = [new() { ServicoId = FakeEstoque.ServicoId }, new() { ServicoId = FakeEstoque.ServicoId }], Pecas = [new() { PecaId = FakeEstoque.PecaId, Quantidade = 1 }] }
    };

    private static OrdensUseCases Criar(FakeRepo repo, FakeCadastro cadastro, FakeEstoque estoque, INotificadorCliente? notifier = null, IFluxoDistribuidoOrdens? fluxo = null, bool distributedEnabled = false) =>
        new(repo, cadastro, estoque, new AbrirOrdemServicoRequestValidator(), new RegistrarDiagnosticoRequestValidator(), notifier ?? new FakeNotifier(), Options.Create(new DistributedFlowOptions { Enabled = distributedEnabled }), fluxo ?? new FakeFluxoDistribuido());
}

internal sealed class FakeRepo : IOrdensServicoRepository
{
    public List<OrdemServico> Ordens { get; } = [];
    public List<Orcamento> Orcamentos { get; } = [];
    public Task<OrdemServico?> ObterOrdemServico(Guid id, CancellationToken ct) => Task.FromResult(Ordens.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<OrdemServico>> ListarOrdensServico(CancellationToken ct) => Task.FromResult<IReadOnlyList<OrdemServico>>(Ordens);
    public Task<IReadOnlyList<OrdemServico>> ListarOrdensServicoPorCliente(Guid clienteId, CancellationToken ct) => Task.FromResult<IReadOnlyList<OrdemServico>>(Ordens.Where(x => x.ClienteId == clienteId).ToList());
    public Task<Orcamento?> ObterOrcamento(Guid id, CancellationToken ct) => Task.FromResult(Orcamentos.FirstOrDefault(x => x.Id == id));
    public Task<Orcamento?> ObterOrcamentoPorOs(Guid ordemServicoId, CancellationToken ct) => Task.FromResult(Orcamentos.FirstOrDefault(x => x.OrdemServicoId == ordemServicoId));
    public Task<Orcamento?> ObterOrcamentoPorTokenAcaoExterna(string token, CancellationToken ct) => Task.FromResult(Orcamentos.FirstOrDefault(x => x.TokenAcaoExterna == token));
    public void AdicionarOrdemServico(OrdemServico ordem) => Ordens.Add(ordem);
    public void AdicionarOrcamento(Orcamento orcamento) => Orcamentos.Add(orcamento);
    public Task Salvar(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeCadastro : ICadastroClient
{
    public Guid ClienteId { get; } = Guid.NewGuid();
    public ClienteDto? Cliente { get; set; }
    public VeiculoDto? Veiculo { get; set; }
    public FakeCadastro()
    {
        Cliente = new(ClienteId, "Cliente", "12345678901", "c@x.com", "11");
        Veiculo = new(Guid.NewGuid(), ClienteId, "ABC1D23", "123", "Modelo", "Marca", 2024);
    }
    public Task<ClienteDto?> ObterCliente(Guid id, CancellationToken ct) => Task.FromResult(Cliente);
    public Task<VeiculoDto?> ObterVeiculo(Guid id, CancellationToken ct) => Task.FromResult(Veiculo);
    public Task<ClienteDto?> ObterClientePorDocumento(string documento, CancellationToken ct) => Task.FromResult(Cliente);
    public Task<VeiculoDto?> ObterVeiculoPorPlaca(string placa, CancellationToken ct) => Task.FromResult(Veiculo);
    public Task<ConsultaServicosResponse> ConsultarServicos(ConsultaServicosRequest request, CancellationToken ct) => Task.FromResult(new ConsultaServicosResponse([new(FakeEstoque.ServicoId, "Troca", 100m, [new(FakeEstoque.PecaId, 2)], [])], []));
}

internal sealed class FakeEstoque : IEstoqueClient
{
    public static readonly Guid ServicoId = Guid.NewGuid();
    public static readonly Guid PecaId = Guid.NewGuid();
    public bool Falhar { get; set; }
    public string MaterialDescricao { get; set; } = "Filtro snapshot";
    public Task<IReadOnlyList<MaterialDto>> ConsultarMateriais(IReadOnlyCollection<(TipoMaterial Tipo, Guid Id)> materiais, CancellationToken ct)
    {
        if (Falhar) throw new HttpRequestException("timeout");
        return Task.FromResult<IReadOnlyList<MaterialDto>>([new(PecaId, MaterialDescricao, 50m, TipoMaterial.Peca)]);
    }
    public Task<DisponibilidadeEstoqueResponse> ConsultarDisponibilidade(DisponibilidadeEstoqueRequest request, CancellationToken ct) => Task.FromResult(new DisponibilidadeEstoqueResponse(true));
}

internal sealed class FakeNotifier : INotificadorCliente
{
    public bool Criado { get; private set; }
    public bool Recusado { get; private set; }
    public Task NotificarOrcamentoCriado(Guid orcamentoId, Guid ordemServicoId, CancellationToken ct) { Criado = true; return Task.CompletedTask; }
    public Task NotificarOrcamentoRecusado(Guid orcamentoId, Guid ordemServicoId, CancellationToken ct) { Recusado = true; return Task.CompletedTask; }
}

internal sealed class FakeFluxoDistribuido : IFluxoDistribuidoOrdens
{
    public int Inicios { get; private set; }
    public Task IniciarAprovacao(Orcamento orcamento, CancellationToken ct) { Inicios++; return Task.CompletedTask; }
    public Task ForcarCompensacao(Guid ordemServicoId, string correlationId, CancellationToken ct) => Task.CompletedTask;
    public Task ReprocessarReserva(Guid ordemServicoId, string correlationId, CancellationToken ct) => Task.CompletedTask;
}

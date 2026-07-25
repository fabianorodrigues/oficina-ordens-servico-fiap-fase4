using System.Globalization;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Oficina.Ordens.Bdd.Support;
using Reqnroll;

namespace Oficina.Ordens.Bdd.Steps;

[Binding]
public sealed class SagaDistribuidaSteps : IDisposable
{
    // Estados de SagaOrdemServico persistidos como inteiro pela conversao do
    // EF Core. Repetidos aqui de proposito: o projeto de BDD nao referencia a
    // Infrastructure, para tratar cada servico como caixa preta.
    private static readonly IReadOnlyDictionary<string, int> EstadosSaga = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["NaoIniciada"] = 1,
        ["PagamentoPendente"] = 2,
        ["PagamentoAprovado"] = 3,
        ["ReservaPendente"] = 4,
        ["Reservada"] = 5,
        ["ReservaRecusada"] = 6,
        ["CompensacaoPendente"] = 7,
        ["Compensada"] = 8,
        ["CompensacaoFalhou"] = 9,
        ["Concluida"] = 10
    };

    private const string EventoReservaConfirmada = "EstoqueReservado";
    private static readonly TimeSpan JanelaDeEstabilidade = TimeSpan.FromSeconds(15);

    private readonly OficinaApiClient _api;
    private readonly string _sufixo;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(10));

    private Guid _pecaId;
    private Guid _servicoId;
    private Guid _ordemId;
    private Guid _orcamentoId;

    public SagaDistribuidaSteps()
    {
        var correlationId = Guid.NewGuid().ToString();
        _api = new OficinaApiClient(correlationId);
        // Cada execucao cria seus proprios dados: cenarios nunca compartilham
        // peca, cliente, veiculo ou ordem.
        _sufixo = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                  + Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);
    }

    private CancellationToken Ct => _cts.Token;

    // ----------------------------------------------------------------- Dado

    [Given(@"um catalogo com uma peca com saldo de (\d+) unidades")]
    public async Task DadoUmaPecaComSaldo(int quantidade)
    {
        var peca = await _api.CriarPecaAsync(new { precoUnitario = 50, descricao = $"Peca BDD {_sufixo}" }, Ct);
        _pecaId = peca.GetProperty("id").GetGuid();

        await _api.AjustarSaldoPecaAsync(_pecaId, quantidade, Ct);
        await AguardarSaldoAsync("saldo inicial da peca", quantidade);
    }

    [Given(@"um servico de catalogo que consome (\d+) unidades dessa peca")]
    public async Task DadoUmServicoQueConsomePeca(int quantidade)
    {
        var servico = await _api.CriarServicoAsync(new
        {
            maoDeObra = 100,
            pecas = new[] { new { id = _pecaId, quantidade } },
            insumos = Array.Empty<object>()
        }, Ct);
        _servicoId = servico.GetProperty("id").GetGuid();
    }

    [Given(@"uma ordem de servico aberta com diagnostico registrado")]
    public async Task DadoUmaOrdemComDiagnostico()
    {
        var documento = ("1" + _sufixo + "0000000").Substring(0, 11);
        var renavam = ("2" + _sufixo + "0000000").Substring(0, 11);
        var placa = ("BD" + _sufixo).Substring(0, 7).ToUpperInvariant();
        var email = $"cliente.bdd+{_sufixo}@example.invalid";

        var cliente = await _api.CriarClienteAsync(new
        {
            cpfCnpj = documento,
            nome = "Cliente BDD",
            email,
            telefone = "11999990000"
        }, Ct);

        await _api.CriarVeiculoAsync(new
        {
            clienteId = cliente.GetProperty("id").GetGuid(),
            placa,
            renavam,
            modelo = new { descricao = "Civic", marca = "Honda", ano = 2022 }
        }, Ct);

        var ordem = await _api.AbrirOrdemAsync(new
        {
            tipoManutencao = "Corretiva",
            cliente = new { nome = "Cliente BDD", documento, email, telefone = "11999990000" },
            veiculo = new { placa, renavam, modelo = new { descricao = "Civic", marca = "Honda", ano = 2022 } },
            itens = new { servicos = Array.Empty<object>(), pecas = Array.Empty<object>(), insumos = Array.Empty<object>() }
        }, Ct);
        _ordemId = ordem.GetProperty("id").GetGuid();

        var diagnostico = await _api.RegistrarDiagnosticoAsync(_ordemId, new
        {
            descricao = "Diagnostico BDD",
            servicoIds = new[] { _servicoId }
        }, Ct);
        _orcamentoId = diagnostico.GetProperty("orcamentoId").GetGuid();
    }

    [Given(@"que o orcamento foi aprovado e a reserva confirmada")]
    public async Task DadoOrcamentoAprovadoEReservaConfirmada()
    {
        await QuandoOrcamentoAprovado();
        await AguardarSaldoAsync("reserva confirmada", 8);
        // Ao consumir EstoqueReservado, o Inbox registra Reservada e conclui a
        // saga na mesma transacao. O estado final observavel e Concluida.
        await AguardarEstadoSagaAsync("Concluida");
    }

    // ---------------------------------------------------------------- Quando

    [When(@"o orcamento e aprovado")]
    public Task QuandoOrcamentoAprovado() => _api.AprovarOrcamentoAsync(_orcamentoId, Ct);

    [When(@"o orcamento e recusado")]
    public Task QuandoOrcamentoRecusado() => _api.RecusarOrcamentoAsync(_orcamentoId, Ct);

    [When(@"a compensacao da ordem e solicitada")]
    public Task QuandoCompensacaoSolicitada() => _api.ForcarCompensacaoAsync(_ordemId, Ct);

    [When(@"o mesmo evento de reserva confirmada e reentregue")]
    public async Task QuandoEventoReentregue()
    {
        var body = await BddEnvironment.QueryScalarAsync<string>(
            "SELECT TOP 1 Body FROM InboxMessages WHERE OrdemServicoId = @ordem AND MessageType = @tipo ORDER BY Id DESC",
            new Dictionary<string, object> { ["@ordem"] = _ordemId, ["@tipo"] = EventoReservaConfirmada },
            Ct);

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException(
                $"Nenhuma mensagem {EventoReservaConfirmada} encontrada no Inbox da ordem {_ordemId}.");
        }

        // Reentrega o envelope exato, com o mesmo messageId. Somente o
        // deduplication id do SQS muda, para que a fila entregue de novo: e o
        // Inbox que precisa descartar, e nao a fila.
        var endpoint = Environment.GetEnvironmentVariable("BDD_SQS_ENDPOINT")
                       ?? throw new InvalidOperationException("BDD_SQS_ENDPOINT ausente.");
        var region = Environment.GetEnvironmentVariable("BDD_AWS_REGION") ?? "us-east-1";
        var credentials = new BasicAWSCredentials(
            Environment.GetEnvironmentVariable("BDD_AWS_ACCESS_KEY_ID") ?? "test",
            Environment.GetEnvironmentVariable("BDD_AWS_SECRET_ACCESS_KEY") ?? "test");

        using var sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig
        {
            ServiceURL = endpoint,
            AuthenticationRegion = region
        });

        var queueUrl = (await sqs.GetQueueUrlAsync("oficina-ordens-eventos.fifo", Ct)).QueueUrl;
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = body!,
            MessageGroupId = _ordemId.ToString(),
            MessageDeduplicationId = Guid.NewGuid().ToString()
        }, Ct);
    }

    // ---------------------------------------------------------------- Entao

    [Then(@"o saldo disponivel da peca passa a ser (\d+) unidades")]
    [When(@"o saldo disponivel da peca passa a ser (\d+) unidades")]
    public Task EntaoSaldoPassaASer(int quantidade)
        => AguardarSaldoAsync($"saldo disponivel igual a {quantidade}", quantidade);

    [Then(@"o saldo disponivel da peca volta para (\d+) unidades")]
    public Task EntaoSaldoVoltaPara(int quantidade)
        => AguardarSaldoAsync($"saldo disponivel de volta em {quantidade}", quantidade);

    [Then(@"o saldo disponivel da peca continua em (\d+) unidades")]
    public async Task EntaoSaldoContinuaEm(int quantidade)
    {
        // Estabilidade, e nao apenas o valor num instante: um efeito duplicado
        // apareceria alguns segundos depois da reentrega.
        var deadline = DateTimeOffset.UtcNow.Add(JanelaDeEstabilidade);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var saldo = await _api.ObterSaldoDisponivelAsync(_pecaId, Ct);
            Assert.Equal(quantidade, saldo);
            await Task.Delay(BddEnvironment.PollInterval, Ct);
        }
    }

    [Then(@"a ordem de servico fica com status ""(.*)""")]
    public async Task EntaoOrdemComStatus(string status)
    {
        await BddEnvironment.WaitUntilAsync(
            $"ordem em status {status}",
            async ct => string.Equals(await _api.ObterStatusOrdemAsync(_ordemId, ct), status, StringComparison.OrdinalIgnoreCase),
            async ct => await _api.ObterStatusOrdemAsync(_ordemId, ct),
            Ct);
    }

    [Then(@"a ordem de servico permanece com status ""(.*)""")]
    public async Task EntaoOrdemPermaneceComStatus(string status)
    {
        var deadline = DateTimeOffset.UtcNow.Add(JanelaDeEstabilidade);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Assert.Equal(status, await _api.ObterStatusOrdemAsync(_ordemId, Ct));
            await Task.Delay(BddEnvironment.PollInterval, Ct);
        }
    }

    [Then(@"a saga da ordem fica no estado ""(.*)""")]
    public Task EntaoSagaNoEstado(string estado) => AguardarEstadoSagaAsync(estado);

    [Then(@"a saga registra a reserva confirmada pelo Estoque")]
    public async Task EntaoSagaRegistraReserva()
    {
        await BddEnvironment.WaitUntilAsync(
            "saga com ReservaId preenchido",
            async ct => await BddEnvironment.QueryScalarAsync<int>(
                "SELECT COUNT(1) FROM SagasOrdensServico WHERE OrdemServicoId = @ordem AND ReservaId IS NOT NULL",
                new Dictionary<string, object> { ["@ordem"] = _ordemId }, ct) == 1,
            ct: Ct);
    }

    [Then(@"o Inbox de Ordens registra uma unica mensagem processada para o evento")]
    public async Task EntaoInboxRegistraUmaUnicaMensagem()
    {
        // O Inbox e quem garante a idempotencia: o mesmo messageId nao pode
        // gerar uma segunda linha nem um segundo efeito.
        var total = await BddEnvironment.QueryScalarAsync<int>(
            "SELECT COUNT(1) FROM InboxMessages WHERE OrdemServicoId = @ordem AND MessageType = @tipo",
            new Dictionary<string, object> { ["@ordem"] = _ordemId, ["@tipo"] = EventoReservaConfirmada },
            Ct);
        Assert.Equal(1, total);

        var processadas = await BddEnvironment.QueryScalarAsync<int>(
            "SELECT COUNT(1) FROM InboxMessages WHERE OrdemServicoId = @ordem AND MessageType = @tipo AND Status = 3",
            new Dictionary<string, object> { ["@ordem"] = _ordemId, ["@tipo"] = EventoReservaConfirmada },
            Ct);
        Assert.Equal(1, processadas);
    }

    [Then(@"nenhuma saga foi iniciada para a ordem")]
    public async Task EntaoNenhumaSagaIniciada()
    {
        var total = await BddEnvironment.QueryScalarAsync<int>(
            "SELECT COUNT(1) FROM SagasOrdensServico WHERE OrdemServicoId = @ordem",
            new Dictionary<string, object> { ["@ordem"] = _ordemId },
            Ct);
        Assert.Equal(0, total);
    }

    // ------------------------------------------------------------- Auxiliares

    private Task AguardarSaldoAsync(string etapa, int esperado)
        => BddEnvironment.WaitUntilAsync(
            etapa,
            async ct => await _api.ObterSaldoDisponivelAsync(_pecaId, ct) == esperado,
            async ct => (await _api.ObterSaldoDisponivelAsync(_pecaId, ct)).ToString(CultureInfo.InvariantCulture),
            Ct);

    private Task AguardarEstadoSagaAsync(string estado)
    {
        if (!EstadosSaga.TryGetValue(estado, out var esperado))
        {
            throw new ArgumentException($"Estado de saga desconhecido: {estado}", nameof(estado));
        }

        return BddEnvironment.WaitUntilAsync(
            $"saga no estado {estado}",
            async ct => await BddEnvironment.QueryScalarAsync<int>(
                "SELECT COUNT(1) FROM SagasOrdensServico WHERE OrdemServicoId = @ordem AND Status = @status",
                new Dictionary<string, object> { ["@ordem"] = _ordemId, ["@status"] = esperado }, ct) == 1,
            async ct => (await BddEnvironment.QueryScalarAsync<int>(
                "SELECT ISNULL(MAX(Status), 0) FROM SagasOrdensServico WHERE OrdemServicoId = @ordem",
                new Dictionary<string, object> { ["@ordem"] = _ordemId }, ct)).ToString(CultureInfo.InvariantCulture),
            Ct);
    }

    public void Dispose()
    {
        _api.Dispose();
        _cts.Dispose();
    }
}

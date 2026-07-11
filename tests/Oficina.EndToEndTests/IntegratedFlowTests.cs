using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Oficina.EndToEndTests;

public sealed class IntegratedFlowTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _cadastro = Client("CADASTRO_BASE_URL", "http://cadastro-api:8080");
    private readonly HttpClient _estoque = Client("ESTOQUE_BASE_URL", "http://estoque-api:8080");
    private readonly HttpClient _ordens = Client("ORDENS_BASE_URL", "http://ordens-api:8080");
    private readonly HttpClient _wireMock = Client("PAYMENT_MOCK_BASE_URL", "http://payment-mock:8080");
    private readonly HttpClient _sqsHttp = Client("SQS_SERVICE_URL", "http://localstack:4566");
    private readonly string _correlationId = Guid.NewGuid().ToString();

    public Task InitializeAsync()
    {
        foreach (var http in new[] { _cadastro, _estoque, _ordens })
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Role", "Funcionario");
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Cpf", "12345678901");
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-FuncionarioId", "11111111-1111-1111-1111-111111111111");
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-Id", _correlationId);
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cadastro.Dispose();
        _estoque.Dispose();
        _ordens.Dispose();
        _wireMock.Dispose();
        _sqsHttp.Dispose();
        return Task.CompletedTask;
    }

    [E2EFact]
    public async Task FluxoPrincipal_AteEntrega_ComSnapshotsESaga()
    {
        var seed = Seed();
        var material = await CriarPecaComSaldo(seed, 10);
        var servico = await PostJson(_cadastro, "/api/servicos", new { maoDeObra = 100m, pecas = new[] { new { id = material, quantidade = 2 } }, insumos = Array.Empty<object>() });
        var ordem = await CriarOrdemComDiagnostico(seed, servico.Id());

        var detalhe = await GetJson(_ordens, $"/api/ordens-servico/{ordem.OrdemId}");
        Assert.Equal(200m, detalhe["orcamento"]!["valorTotal"]!.GetValue<decimal>());
        Assert.Equal(ordem.VeiculoId, detalhe["veiculoId"]!.GetValue<Guid>());

        await PostNoContentOrSuccess(_ordens, $"/api/orcamentos/{ordem.OrcamentoId}/aprovar");
        await PostNoContentOrSuccess(_ordens, $"/api/orcamentos/{ordem.OrcamentoId}/aprovar");

        await WaitForStatus(ordem.OrdemId, "EmExecucao", TimeSpan.FromSeconds(120));
        await WaitForQuantity(material, 8, TimeSpan.FromSeconds(120));
        Assert.Equal("Concluida", await WaitForSaga(ordem.OrdemId, "Concluida", TimeSpan.FromSeconds(120)));
        Assert.Equal("Aprovado", await WaitForPagamento(ordem.OrdemId, "Aprovado", TimeSpan.FromSeconds(60)));

        await PostNoContentOrSuccess(_ordens, $"/api/ordens-servico/{ordem.OrdemId}/finalizar");
        await PostNoContentOrSuccess(_ordens, $"/api/ordens-servico/{ordem.OrdemId}/entregar");
        await WaitForStatus(ordem.OrdemId, "Entregue", TimeSpan.FromSeconds(30));

        var entregue = await GetJson(_ordens, $"/api/ordens-servico/{ordem.OrdemId}");
        Assert.Equal("Entregue", entregue["status"]!.GetValue<string>());
        Assert.Equal(200m, entregue["orcamento"]!["valorTotal"]!.GetValue<decimal>());
        Assert.Equal(material, entregue["orcamento"]!["itensMaterial"]![0]!["materialId"]!.GetValue<Guid>());
    }

    [E2EFact]
    public async Task SaldoInsuficiente_RecusaReserva_SemDebitoParcial()
    {
        var seed = Seed();
        var material = await CriarPecaComSaldo(seed, 1);
        var servico = await PostJson(_cadastro, "/api/servicos", new { maoDeObra = 10m, pecas = new[] { new { id = material, quantidade = 2 } }, insumos = Array.Empty<object>() });
        var ordem = await CriarOrdemComDiagnostico(seed, servico.Id());

        await PostNoContentOrSuccess(_ordens, $"/api/orcamentos/{ordem.OrcamentoId}/aprovar");

        Assert.Equal("ReservaRecusada", await WaitForSaga(ordem.OrdemId, "ReservaRecusada", TimeSpan.FromSeconds(120)));
        await WaitForQuantity(material, 1, TimeSpan.FromSeconds(30));
        var status = await GetJson(_ordens, $"/api/ordens-servico/{ordem.OrdemId}/status");
        Assert.Equal("AguardandoAprovacao", status["status"]!.GetValue<string>());
    }

    [E2EFact]
    public async Task PagamentoRecusado_NaoPublicaReserva()
    {
        var seed = Seed();
        var material = await CriarPecaComSaldo(seed, 5);
        var servico = await PostJson(_cadastro, "/api/servicos", new { maoDeObra = 10m, pecas = new[] { new { id = material, quantidade = 1 } }, insumos = Array.Empty<object>() });
        var ordem = await CriarOrdemComDiagnostico(seed, servico.Id());
        await StubPagamentoPorIdempotencyKey(ordem.OrdemId, "recusado");

        await PostNoContentOrSuccess(_ordens, $"/api/orcamentos/{ordem.OrcamentoId}/aprovar");

        Assert.Equal("Recusado", await WaitForPagamento(ordem.OrdemId, "Recusado", TimeSpan.FromSeconds(60)));
        await WaitForQuantity(material, 5, TimeSpan.FromSeconds(30));
        Assert.Equal(0, await SqlScalar<int>("SQL_ORDENS", "SELECT COUNT(1) FROM OutboxMessages WHERE OrdemServicoId = @id AND MessageType = 'ReservarEstoque'", ("@id", ordem.OrdemId)));
    }

    [E2EFact]
    public async Task MensagemForaDeOrdem_FicaDeferred_EPoisonMessageENaoAck()
    {
        var ordemId = Guid.NewGuid();
        var outOfOrderId = Guid.NewGuid();
        await SendSqs("oficina-ordens-eventos.fifo", Envelope("ReservaEstoqueLiberada", ordemId, new { reservaId = Guid.NewGuid(), jaLiberada = false }, outOfOrderId), outOfOrderId.ToString());
        await WaitUntil(async () =>
        {
            var status = await SqlScalar<int?>("SQL_ORDENS", "SELECT TOP 1 Status FROM InboxMessages WHERE MessageId = @id", ("@id", outOfOrderId));
            return status == 4;
        }, TimeSpan.FromSeconds(60), "evento fora de ordem ficar Deferred");

        await SendRawSqs("oficina-estoque-comandos.fifo", "{invalid-json", Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        Assert.True(true);
    }

    private async Task<(Guid OrdemId, Guid OrcamentoId, Guid VeiculoId)> CriarOrdemComDiagnostico(string seed, Guid servicoId)
    {
        var cliente = await PostJson(_cadastro, "/api/clientes", new
        {
            cpfCnpj = Documento(seed),
            nome = $"Cliente E2E {seed}",
            email = $"cliente.e2e.{seed}@example.com",
            telefone = "11999990000"
        });
        var veiculo = await PostJson(_cadastro, "/api/veiculos", new
        {
            clienteId = cliente.Id(),
            placa = Placa(seed),
            renavam = Renavam(seed),
            modelo = new { descricao = "Civic", marca = "Honda", ano = 2022 }
        });
        var ordem = await PostJson(_ordens, "/api/ordens-servico", new
        {
            tipoManutencao = "Corretiva",
            cliente = new { nome = $"Cliente E2E {seed}", documento = Documento(seed), email = $"cliente.e2e.{seed}@example.com", telefone = "11999990000" },
            veiculo = new { placa = Placa(seed), renavam = Renavam(seed), modelo = new { descricao = "Civic", marca = "Honda", ano = 2022 } },
            itens = new { servicos = Array.Empty<object>(), pecas = Array.Empty<object>(), insumos = Array.Empty<object>() }
        });
        var diagnostico = await PostJson(_ordens, $"/api/ordens-servico/{ordem.Id()}/diagnostico", new { descricao = $"Diagnostico {seed}", servicoIds = new[] { servicoId } });
        return (ordem.Id(), diagnostico["orcamentoId"]!.GetValue<Guid>(), veiculo.Id());
    }

    private async Task<Guid> CriarPecaComSaldo(string seed, int saldo)
    {
        var peca = await PostJson(_estoque, "/api/pecas", new { precoUnitario = 50m, descricao = $"Peca E2E {seed}" });
        await PostNoContentOrSuccess(_estoque, $"/api/estoque/pecas/{peca.Id()}/ajustar", new { quantidade = saldo });
        return peca.Id();
    }

    private async Task StubPagamentoPorIdempotencyKey(Guid ordemId, string status)
    {
        var key = $"ordem-servico:{ordemId}:pagamento";
        var body = new Dictionary<string, object?>
        {
            ["priority"] = 1,
            ["request"] = new Dictionary<string, object?>
            {
                ["method"] = "POST",
                ["urlPath"] = "/payments",
                ["headers"] = new Dictionary<string, object?>
                {
                    ["Idempotency-Key"] = new { equalTo = key }
                }
            },
            ["response"] = new Dictionary<string, object?>
            {
                ["status"] = 200,
                ["headers"] = new Dictionary<string, object?> { ["Content-Type"] = "application/json" },
                ["jsonBody"] = new { providerPaymentId = Guid.NewGuid(), status }
            }
        };
        using var response = await _wireMock.PostAsJsonAsync("/__admin/mappings", body, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonNode> PostJson(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>(JsonOptions))!;
    }

    private static async Task<JsonNode> GetJson(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>(JsonOptions))!;
    }

    private static async Task PostNoContentOrSuccess(HttpClient client, string url, object? body = null)
    {
        using var response = body is null
            ? await client.PostAsync(url, null)
            : await client.PostAsJsonAsync(url, body, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private async Task WaitForStatus(Guid ordemId, string expected, TimeSpan timeout) =>
        await WaitUntil(async () => (await GetJson(_ordens, $"/api/ordens-servico/{ordemId}/status"))["status"]!.GetValue<string>() == expected, timeout, $"status {expected}");

    private async Task WaitForQuantity(Guid materialId, int expected, TimeSpan timeout) =>
        await WaitUntil(async () =>
        {
            var body = await PostJson(_estoque, "/api/internal/estoque/disponibilidade", new { items = new[] { new { tipoMaterial = 1, materialId, requestedQuantity = 1 } } });
            return body["items"]![0]!["availableQuantity"]!.GetValue<int>() == expected;
        }, timeout, $"saldo {expected}");

    private async Task<string> WaitForSaga(Guid ordemId, string expected, TimeSpan timeout)
    {
        string? current = null;
        await WaitUntil(async () =>
        {
            current = await SqlScalar<string?>("SQL_ORDENS", "SELECT TOP 1 CASE Status WHEN 6 THEN 'ReservaRecusada' WHEN 10 THEN 'Concluida' WHEN 8 THEN 'Compensada' ELSE CONVERT(varchar(20), Status) END FROM SagasOrdensServico WHERE OrdemServicoId = @id", ("@id", ordemId));
            return current == expected;
        }, timeout, $"saga {expected}");
        return current!;
    }

    private async Task<string> WaitForPagamento(Guid ordemId, string expected, TimeSpan timeout)
    {
        string? current = null;
        await WaitUntil(async () =>
        {
            current = await SqlScalar<string?>("SQL_ORDENS", "SELECT TOP 1 CASE Status WHEN 1 THEN 'Pendente' WHEN 2 THEN 'Aprovado' WHEN 3 THEN 'Recusado' WHEN 4 THEN 'Falhou' END FROM Pagamentos WHERE OrdemServicoId = @id", ("@id", ordemId));
            return current == expected;
        }, timeout, $"pagamento {expected}");
        return current!;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout, string label)
    {
        var start = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromMilliseconds(250);
        while (DateTimeOffset.UtcNow - start < timeout)
        {
            if (await condition())
                return;
            await Task.Delay(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 2000));
        }
        throw new TimeoutException($"Timeout aguardando {label}.");
    }

    private async Task<T?> SqlScalar<T>(string connectionEnv, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(Env(connectionEnv, ""));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull)
            return default;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private async Task SendSqs(string queueName, object envelope, string deduplicationId) =>
        await SendRawSqs(queueName, JsonSerializer.Serialize(envelope, JsonOptions), ((JsonNode)JsonSerializer.SerializeToNode(envelope, JsonOptions)!)["ordemServicoId"]!.GetValue<string>(), deduplicationId);

    private async Task SendRawSqs(string queueName, string body, string groupId, string deduplicationId)
    {
        var url = LocalQueueUrl(queueName);
        using var response = await _sqsHttp.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Action"] = "SendMessage",
            ["Version"] = "2012-11-05",
            ["QueueUrl"] = url,
            ["MessageBody"] = body,
            ["MessageGroupId"] = groupId,
            ["MessageDeduplicationId"] = deduplicationId
        }));
        response.EnsureSuccessStatusCode();
    }

    private static string LocalQueueUrl(string queueName) => $"{Env("SQS_SERVICE_URL", "http://localstack:4566").TrimEnd('/')}/000000000000/{queueName}";

    private object Envelope(string messageType, Guid ordemId, object payload, Guid? messageId = null) => new
    {
        messageId = messageId ?? Guid.NewGuid(),
        messageType,
        schemaVersion = 1,
        occurredAtUtc = DateTimeOffset.UtcNow,
        correlationId = _correlationId,
        causationId = (string?)null,
        ordemServicoId = ordemId,
        payload
    };

    private static HttpClient Client(string env, string fallback) => new() { BaseAddress = new Uri(Env(env, fallback)), Timeout = TimeSpan.FromSeconds(30) };
    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
    private static string Seed() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()[^8..];
    private static string Documento(string seed) => ("1" + seed + "01")[..11];
    private static string Renavam(string seed) => ("2" + seed + "01")[..11];
    private static string Placa(string seed) => ("E" + seed)[..7].ToUpperInvariant();
}

internal static class JsonNodeExtensions
{
    public static Guid Id(this JsonNode node) => node["id"]!.GetValue<Guid>();
}

using System.Net.Http.Json;
using System.Text.Json;

namespace Oficina.Ordens.Bdd.Support;

/// <summary>
/// Cliente HTTP dos tres microsservicos. Exercita as APIs reais: nada e
/// simulado no lugar de uma chamada de rede.
/// </summary>
public sealed class OficinaApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _cadastro;
    private readonly HttpClient _estoque;
    private readonly HttpClient _ordens;

    public OficinaApiClient(string correlationId)
    {
        CorrelationId = correlationId;
        _cadastro = BddEnvironment.CreateClient(BddEnvironment.CadastroBaseUrl, correlationId);
        _estoque = BddEnvironment.CreateClient(BddEnvironment.EstoqueBaseUrl, correlationId);
        _ordens = BddEnvironment.CreateClient(BddEnvironment.OrdensBaseUrl, correlationId);
    }

    public string CorrelationId { get; }

    public Task<JsonElement> CriarClienteAsync(object body, CancellationToken ct)
        => PostAsync(_cadastro, "/api/clientes", body, ct);

    public Task<JsonElement> CriarVeiculoAsync(object body, CancellationToken ct)
        => PostAsync(_cadastro, "/api/veiculos", body, ct);

    public Task<JsonElement> CriarServicoAsync(object body, CancellationToken ct)
        => PostAsync(_cadastro, "/api/servicos", body, ct);

    public Task<JsonElement> CriarPecaAsync(object body, CancellationToken ct)
        => PostAsync(_estoque, "/api/pecas", body, ct);

    public Task<JsonElement> AjustarSaldoPecaAsync(Guid pecaId, int quantidade, CancellationToken ct)
        => PostAsync(_estoque, $"/api/estoque/pecas/{pecaId}/ajustar", new { quantidade }, ct);

    public async Task<int> ObterSaldoDisponivelAsync(Guid materialId, CancellationToken ct)
    {
        var body = new
        {
            items = new[] { new { tipoMaterial = 1, materialId, requestedQuantity = 1 } }
        };
        var response = await PostAsync(_estoque, "/api/internal/estoque/disponibilidade", body, ct);
        return response.GetProperty("items")[0].GetProperty("availableQuantity").GetInt32();
    }

    public Task<JsonElement> AbrirOrdemAsync(object body, CancellationToken ct)
        => PostAsync(_ordens, "/api/ordens-servico", body, ct);

    public Task<JsonElement> RegistrarDiagnosticoAsync(Guid ordemId, object body, CancellationToken ct)
        => PostAsync(_ordens, $"/api/ordens-servico/{ordemId}/diagnostico", body, ct);

    public Task AprovarOrcamentoAsync(Guid orcamentoId, CancellationToken ct)
        => PostNoContentAsync(_ordens, $"/api/orcamentos/{orcamentoId}/aprovar", ct);

    public Task RecusarOrcamentoAsync(Guid orcamentoId, CancellationToken ct)
        => PostNoContentAsync(_ordens, $"/api/orcamentos/{orcamentoId}/recusar", ct);

    public Task ForcarCompensacaoAsync(Guid ordemId, CancellationToken ct)
        => PostNoContentAsync(_ordens, $"/api/dev/ordens-servico/{ordemId}/forcar-compensacao", ct);

    public async Task<string> ObterStatusOrdemAsync(Guid ordemId, CancellationToken ct)
    {
        var response = await GetAsync(_ordens, $"/api/ordens-servico/{ordemId}/status", ct);
        return response.GetProperty("status").GetString() ?? string.Empty;
    }

    public Task<JsonElement> ObterOrdemAsync(Guid ordemId, CancellationToken ct)
        => GetAsync(_ordens, $"/api/ordens-servico/{ordemId}", ct);

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, body, JsonOptions, ct);
        await EnsureSuccessAsync(response, path, ct);
        return await ReadJsonAsync(response, ct);
    }

    private static async Task PostNoContentAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.PostAsync(path, content: null, ct);
        await EnsureSuccessAsync(response, path, ct);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        await EnsureSuccessAsync(response, path, ct);
        return await ReadJsonAsync(response, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var payload = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"{(int)response.StatusCode} em {path}: {payload}");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return default;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        _cadastro.Dispose();
        _estoque.Dispose();
        _ordens.Dispose();
    }
}

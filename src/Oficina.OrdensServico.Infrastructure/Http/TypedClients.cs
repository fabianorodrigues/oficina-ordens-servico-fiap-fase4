using System.Net;
using System.Net.Http.Json;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Application.Contracts;
using Oficina.OrdensServico.Domain.Ordens;
using Oficina.OrdensServico.Infrastructure.Observability;

namespace Oficina.OrdensServico.Infrastructure.Http;

public sealed class CadastroClient(HttpClient http, IOrdensBusinessMetrics metrics) : ICadastroClient
{
    public Task<ClienteDto?> ObterCliente(Guid id, CancellationToken ct) => Get<ClienteDto>($"/api/internal/clientes/{id}", ct);
    public Task<VeiculoDto?> ObterVeiculo(Guid id, CancellationToken ct) => Get<VeiculoDto>($"/api/internal/veiculos/{id}", ct);
    public Task<ClienteDto?> ObterClientePorDocumento(string documento, CancellationToken ct) => Get<ClienteDto>($"/api/internal/clientes/documento/{Uri.EscapeDataString(documento)}", ct);
    public Task<VeiculoDto?> ObterVeiculoPorPlaca(string placa, CancellationToken ct) => Get<VeiculoDto>($"/api/internal/veiculos/placa/{Uri.EscapeDataString(placa)}", ct);

    public async Task<ConsultaServicosResponse> ConsultarServicos(ConsultaServicosRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/api/internal/servicos/consulta", request, ct);
            response.EnsureSuccessStatusCode();
            var cadastro = (await response.Content.ReadFromJsonAsync<CadastroConsultaServicosResponse>(cancellationToken: ct))!;
            return new ConsultaServicosResponse(
                cadastro.Encontrados.Select(x => new ServicoDto(
                    x.Id,
                    string.Empty,
                    x.MaoDeObra,
                    x.Pecas.Select(p => new ReceitaMaterialDto(p.ReferenciaId, p.Quantidade)).ToList(),
                    x.Insumos.Select(i => new ReceitaMaterialDto(i.ReferenciaId, i.Quantidade)).ToList())).ToList(),
                cadastro.Ausentes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            metrics.FalhaIntegracao(OficinaTelemetry.Integrations.Cadastro, OficinaTelemetry.Operations.Query);
            throw;
        }
    }

    private async Task<T?> Get<T>(string uri, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(uri, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            if ((int)response.StatusCode >= 500) throw new HttpRequestException("Falha transitoria no Cadastro.", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 404 nao passa por aqui: ausencia de recurso e resposta valida de
            // negocio, nao falha de integracao.
            metrics.FalhaIntegracao(OficinaTelemetry.Integrations.Cadastro, OficinaTelemetry.Operations.Query);
            throw;
        }
    }
}

public sealed class EstoqueClient(HttpClient http, IOrdensBusinessMetrics metrics) : IEstoqueClient
{
    public async Task<IReadOnlyList<MaterialDto>> ConsultarMateriais(IReadOnlyCollection<(TipoMaterial Tipo, Guid Id)> materiais, CancellationToken ct)
    {
        var ids = materiais.Select(x => x.Id).Distinct().ToList();
        try
        {
            using var response = await http.PostAsJsonAsync("/api/internal/materiais/consulta", new { ids }, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IReadOnlyList<MaterialDto>>(cancellationToken: ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            metrics.FalhaIntegracao(OficinaTelemetry.Integrations.Estoque, OficinaTelemetry.Operations.Query);
            throw;
        }
    }

    public async Task<DisponibilidadeEstoqueResponse> ConsultarDisponibilidade(DisponibilidadeEstoqueRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/api/internal/estoque/disponibilidade", request, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<DisponibilidadeEstoqueResponse>(cancellationToken: ct))!;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            metrics.FalhaIntegracao(OficinaTelemetry.Integrations.Estoque, OficinaTelemetry.Operations.Query);
            throw;
        }
    }
}

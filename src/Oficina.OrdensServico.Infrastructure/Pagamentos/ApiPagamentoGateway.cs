using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public sealed class ApiPagamentoGateway(HttpClient http, IConfiguration configuration, ILogger<ApiPagamentoGateway> logger) : IPagamentoGateway
{
    public async Task<PagamentoGatewayResult> Processar(PagamentoGatewayRequest request, CancellationToken ct)
    {
        var scenario = configuration["Payments:Scenario"] ?? "aprovado";
        using var message = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new
            {
                ordemServicoId = request.OrdemServicoId,
                amount = request.Valor,
                scenario
            })
        };
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", request.ChaveIdempotencia);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Timeout transitorio no provedor de pagamento.", null, HttpStatusCode.RequestTimeout);
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            throw new HttpRequestException("Falha transitoria no provedor de pagamento.", null, response.StatusCode);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Accepted)
        {
            logger.LogWarning("Pagamento recusado pelo provedor. StatusCode={StatusCode}", (int)response.StatusCode);
            return new PagamentoGatewayResult(ResultadoPagamentoStatus.Recusado, null, "Pagamento recusado pelo provedor.");
        }

        var body = await response.Content.ReadFromJsonAsync<PagamentoProviderResponse>(cancellationToken: ct)
            ?? new PagamentoProviderResponse(null, "pendente");
        return (body.Status ?? "pendente").ToLowerInvariant() switch
        {
            "aprovado" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Aprovado, body.ProviderPaymentId, null),
            "recusado" => new PagamentoGatewayResult(ResultadoPagamentoStatus.Recusado, body.ProviderPaymentId, "Pagamento recusado."),
            _ => new PagamentoGatewayResult(ResultadoPagamentoStatus.Pendente, body.ProviderPaymentId, null)
        };
    }

    private sealed record PagamentoProviderResponse(string? ProviderPaymentId, string? Status);
}

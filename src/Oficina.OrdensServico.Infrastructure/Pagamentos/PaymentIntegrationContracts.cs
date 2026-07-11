using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public sealed class PaymentContractPendingException(string message) : InvalidOperationException(message);

public sealed record PaymentIntegrationContext(string CorrelationId, string IdempotencyKey, int Attempt);

public sealed record PaymentSubmissionResult(ResultadoPagamentoStatus Status, string? ExternalPaymentId, string? Motivo);

public sealed record PaymentWebhookEvent(
    string ExternalEventId,
    string PaymentOperationId,
    string? ExternalPaymentId,
    ResultadoPagamentoStatus Status,
    DateTimeOffset OccurredAtUtc,
    string? ErrorCode);

public interface IExternalPaymentContractMapper
{
    HttpRequestMessage CreatePaymentRequest(PagamentoGatewayRequest request, Uri webhookUrl, PaymentIntegrationContext context);
    Task<PaymentSubmissionResult> ReadSubmissionResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken);
    Task<PaymentWebhookEvent> ReadWebhookAsync(Stream body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}

public sealed class PendingExternalPaymentContractMapper : IExternalPaymentContractMapper
{
    public HttpRequestMessage CreatePaymentRequest(PagamentoGatewayRequest request, Uri webhookUrl, PaymentIntegrationContext context) =>
        throw new PaymentContractPendingException("Contrato da API externa de pagamentos pendente.");

    public Task<PaymentSubmissionResult> ReadSubmissionResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        throw new PaymentContractPendingException("Contrato da resposta da API externa de pagamentos pendente.");

    public Task<PaymentWebhookEvent> ReadWebhookAsync(Stream body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) =>
        throw new PaymentContractPendingException("Contrato do webhook externo de pagamentos pendente.");
}

public sealed record WebhookAuthenticationResult(bool Succeeded, string? FailureReason)
{
    public static WebhookAuthenticationResult Success() => new(true, null);
    public static WebhookAuthenticationResult Failure(string reason) => new(false, reason);
}

public interface IPaymentWebhookAuthenticator
{
    Task<WebhookAuthenticationResult> AuthenticateAsync(
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken);
}

public sealed class PendingPaymentWebhookAuthenticator(IConfiguration configuration) : IPaymentWebhookAuthenticator
{
    public Task<WebhookAuthenticationResult> AuthenticateAsync(
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.GetValue("Payments:ExternalWebhookEnabled", false))
            return Task.FromResult(WebhookAuthenticationResult.Failure("Autenticacao do webhook pendente de contrato externo."));

        return Task.FromResult(WebhookAuthenticationResult.Failure("Webhook externo desabilitado."));
    }
}

public static class PaymentHashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash[..16]);
    }
}

public sealed class ExternalPaymentApiGateway(
    HttpClient http,
    IConfiguration configuration,
    IExternalPaymentContractMapper mapper,
    ILogger<ExternalPaymentApiGateway> logger) : IPagamentoGateway
{
    public async Task<PagamentoGatewayResult> Processar(PagamentoGatewayRequest request, CancellationToken ct)
    {
        if (!configuration.GetValue("Payments:ExternalApiEnabled", false))
            throw new InvalidOperationException("API externa de pagamentos desabilitada.");

        var webhookUrl = BuildWebhookUrl(configuration);
        var attempts = Math.Max(0, configuration.GetValue("Payments:MaxRetryAttempts", 2));
        for (var attempt = 0; ; attempt++)
        {
            var context = new PaymentIntegrationContext(request.CorrelationId, request.ChaveIdempotencia, attempt + 1);
            using var message = mapper.CreatePaymentRequest(request, webhookUrl, context);
            message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
            message.Headers.TryAddWithoutValidation("Idempotency-Key", request.ChaveIdempotencia);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(message, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < attempts)
            {
                logger.LogWarning("Timeout transitorio na API externa de pagamentos. RetryAttempt={RetryAttempt}", attempt + 1);
                continue;
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                logger.LogWarning("Falha temporaria na API externa de pagamentos. RetryAttempt={RetryAttempt}", attempt + 1);
                continue;
            }

            if (IsTransient(response.StatusCode) && attempt < attempts)
            {
                logger.LogWarning("Resposta transitoria da API externa de pagamentos. StatusCode={StatusCode} RetryAttempt={RetryAttempt}",
                    (int)response.StatusCode, attempt + 1);
                continue;
            }

            var submission = await mapper.ReadSubmissionResponseAsync(response, ct);
            return new PagamentoGatewayResult(submission.Status, submission.ExternalPaymentId, submission.Motivo);
        }
    }

    public Task<PagamentoCompensacaoResult> Compensar(PagamentoCompensacaoRequest request, CancellationToken ct) =>
        throw new PaymentContractPendingException("Contrato de compensacao da API externa de pagamentos pendente.");

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static Uri BuildWebhookUrl(IConfiguration configuration)
    {
        var baseUrl = configuration["Application:PublicBaseUrl"];
        var path = configuration["Payments:WebhookPath"] ?? "/api/webhooks/payments";
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Application:PublicBaseUrl deve ser configurada para integracao externa.");

        var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Webhook publico deve usar URL HTTPS absoluta.");

        return uri;
    }
}

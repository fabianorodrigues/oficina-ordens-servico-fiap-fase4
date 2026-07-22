using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/webhooks/payments")]
public sealed class PaymentWebhooksController(
    IConfiguration configuration,
    IPaymentWebhookAuthenticator authenticator,
    IExternalPaymentContractMapper mapper,
    IPaymentWebhookHandler handler,
    ILogger<PaymentWebhooksController> logger) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Payments:ExternalWebhookEnabled", false))
            return NotFound();

        var maxBytes = configuration.GetValue("Payments:MaxWebhookBodyBytes", 65536);
        var body = await ReadBody(maxBytes, cancellationToken);
        if (body is null || body.Length == 0)
            return BadRequest(new { error = "invalid_webhook_body" });

        var headers = Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var auth = await authenticator.AuthenticateAsync(headers, body, cancellationToken);
        if (!auth.Succeeded)
            return Unauthorized(new { error = "invalid_webhook_authenticity" });

        PaymentWebhookEvent paymentEvent;
        try
        {
            await using var stream = new MemoryStream(body);
            paymentEvent = await mapper.ReadWebhookAsync(stream, headers, cancellationToken);
        }
        catch (PaymentContractPendingException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "payment_contract_pending" });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook de pagamento invalido.");
            return BadRequest(new { error = "invalid_webhook_payload" });
        }

        var correlationId = Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier;
        var result = await handler.HandleAsync(paymentEvent, PaymentHashing.Sha256Hex(body), correlationId, cancellationToken);
        return result.Status switch
        {
            PaymentWebhookHandleStatus.Processed or PaymentWebhookHandleStatus.Duplicate => Ok(new { status = "accepted" }),
            PaymentWebhookHandleStatus.NotFound => NotFound(new { error = "payment_operation_not_found" }),
            PaymentWebhookHandleStatus.Conflict => Conflict(new { error = "payment_webhook_conflict" }),
            PaymentWebhookHandleStatus.InvalidTransition => Conflict(new { error = "payment_webhook_invalid_transition" }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "payment_webhook_unavailable" })
        };
    }

    private async Task<byte[]?> ReadBody(int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
            return null;

        await using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            if (memory.Length + read > maxBytes)
                return null;

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Oficina.OrdensServico.Infrastructure.Messaging;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public enum PaymentWebhookHandleStatus
{
    Processed,
    Duplicate,
    Conflict,
    NotFound,
    InvalidTransition
}

public sealed record PaymentWebhookHandleResult(PaymentWebhookHandleStatus Status, string? Reason);

public interface IPaymentWebhookHandler
{
    Task<PaymentWebhookHandleResult> HandleAsync(
        PaymentWebhookEvent paymentEvent,
        string payloadHash,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class PaymentWebhookHandler(OrdensServicoDbContext db, ILogger<PaymentWebhookHandler> logger) : IPaymentWebhookHandler
{
    public async Task<PaymentWebhookHandleResult> HandleAsync(
        PaymentWebhookEvent paymentEvent,
        string payloadHash,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(paymentEvent.ExternalEventId))
            return new PaymentWebhookHandleResult(PaymentWebhookHandleStatus.Conflict, "ExternalEventId ausente.");

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var inboxId = PaymentHashing.StableGuid($"payment-webhook:{paymentEvent.ExternalEventId}");
        var inbox = await db.InboxMessages.FirstOrDefaultAsync(x => x.MessageId == inboxId, cancellationToken);
        var hashBody = $"sha256:{payloadHash}";
        if (inbox is not null)
        {
            if (!string.Equals(inbox.Body, hashBody, StringComparison.Ordinal))
            {
                logger.LogWarning("Conflito de webhook de pagamento. ExternalEventId={ExternalEventId}", paymentEvent.ExternalEventId);
                return new PaymentWebhookHandleResult(PaymentWebhookHandleStatus.Conflict, "Callback duplicado com payload divergente.");
            }

            return new PaymentWebhookHandleResult(PaymentWebhookHandleStatus.Duplicate, null);
        }

        var pagamento = await FindPagamento(paymentEvent, cancellationToken);
        if (pagamento is null)
            return new PaymentWebhookHandleResult(PaymentWebhookHandleStatus.NotFound, "Operacao de pagamento inexistente.");

        inbox = new InboxMessage(inboxId, "PaymentWebhook", pagamento.OrdemServicoId, correlationId, hashBody);
        inbox.Claim(DateTimeOffset.UtcNow.AddSeconds(30));
        db.InboxMessages.Add(inbox);

        var saga = await db.SagasOrdensServico.FirstOrDefaultAsync(x => x.OrdemServicoId == pagamento.OrdemServicoId, cancellationToken);
        if (saga is null)
            return new PaymentWebhookHandleResult(PaymentWebhookHandleStatus.NotFound, "Saga inexistente.");

        if (saga.Status is StatusSagaOrdemServico.Concluida or StatusSagaOrdemServico.Compensada)
            return new PaymentWebhookHandleResult(PaymentWebhookHandleStatus.InvalidTransition, "Saga finalizada.");

        var previousState = saga.Status;
        switch (paymentEvent.Status)
        {
            case ResultadoPagamentoStatus.Aprovado when pagamento.Status == StatusPagamentoOrdem.Pendente:
                pagamento.MarcarAprovado(paymentEvent.ExternalPaymentId ?? paymentEvent.PaymentOperationId);
                saga.PagamentoAprovado();
                db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, pagamento.OrdemServicoId, previousState, saga.Status, "PaymentWebhookApproved", paymentEvent.ExternalEventId, "Pagamento aprovado por webhook externo."));
                break;
            case ResultadoPagamentoStatus.Recusado when pagamento.Status == StatusPagamentoOrdem.Pendente:
                pagamento.MarcarRecusado(paymentEvent.ExternalPaymentId, paymentEvent.ErrorCode ?? "Pagamento recusado por webhook externo.");
                db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, pagamento.OrdemServicoId, previousState, saga.Status, "PaymentWebhookRejected", paymentEvent.ExternalEventId, "Pagamento recusado por webhook externo."));
                break;
            default:
                return new PaymentWebhookHandleResult(PaymentWebhookHandleStatus.InvalidTransition, "Transicao de pagamento invalida.");
        }

        inbox.MarkProcessed();
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new PaymentWebhookHandleResult(PaymentWebhookHandleStatus.Processed, null);
    }

    private Task<PagamentoOrdem?> FindPagamento(PaymentWebhookEvent paymentEvent, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(paymentEvent.PaymentOperationId, out var paymentId))
            return db.Pagamentos.FirstOrDefaultAsync(x => x.Id == paymentId, cancellationToken);

        return db.Pagamentos.FirstOrDefaultAsync(x => x.PagamentoExternoId == paymentEvent.PaymentOperationId, cancellationToken);
    }
}

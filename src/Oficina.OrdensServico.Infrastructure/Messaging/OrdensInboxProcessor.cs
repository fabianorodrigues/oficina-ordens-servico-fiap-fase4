using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oficina.OrdensServico.Domain.Oficina;
using Oficina.OrdensServico.Infrastructure.Pagamentos;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

internal sealed class OrdensInboxProcessor(
    IServiceScopeFactory scopes,
    IAmazonSQS sqs,
    Microsoft.Extensions.Options.IOptions<SqsMessagingOptions> options,
    ILogger<OrdensInboxProcessor> logger) : SqsBackgroundService(logger)
{
    protected override async Task ExecuteOnce(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdensServicoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var inbox = await db.InboxMessages
            .Where(x => x.Status == InboxMessageStatus.Received || (x.Status == InboxMessageStatus.Deferred && x.LockedUntilUtc < now) || (x.Status == InboxMessageStatus.Processing && x.LockedUntilUtc < now))
            .OrderBy(x => x.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (inbox is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return;
        }

        inbox.Claim(now.AddSeconds(30));
        await db.SaveChangesAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var envelope = MessageJson.ParseAndValidate(inbox.Body);
            await ProcessarEventoEstoque(db, inbox, envelope, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogError(ex, "Falha ao processar Inbox {MessageId}.", inbox.MessageId);
            if (inbox.Attempts >= 3)
            {
                await PublishExplicitDlq(inbox, ct);
                inbox.MarkFailed(ex.Message, deadLetter: true);
            }
            else
            {
                inbox.MarkFailed(ex.Message, deadLetter: false);
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task PublishExplicitDlq(InboxMessage inbox, CancellationToken ct)
    {
        var dlqUrl = await QueueUrlResolver.Resolve(sqs, options.Value, options.Value.EventsDlqQueueName, ct);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = dlqUrl,
            MessageBody = inbox.Body,
            MessageGroupId = inbox.OrdemServicoId.ToString(),
            MessageDeduplicationId = inbox.MessageId.ToString()
        }, ct);
    }

    private static async Task ProcessarEventoEstoque(OrdensServicoDbContext db, InboxMessage inbox, MessageEnvelope envelope, CancellationToken ct)
    {
        var saga = await db.SagasOrdensServico.FirstOrDefaultAsync(x => x.OrdemServicoId == inbox.OrdemServicoId, ct);
        if (saga is null)
        {
            inbox.MarkDeferred("Saga ainda nao existe para o evento.");
            return;
        }

        if (inbox.MessageType == OrdensMessageTypes.EstoqueReservado)
        {
            var payload = envelope.Payload.Deserialize<EstoqueReservadoPayload>(MessageJson.Options)
                ?? throw new InvalidOperationException("Payload de reserva confirmada invalido.");
            saga.Reservada(payload.ReservaId);
            saga.Concluir();
            var os = await db.OrdensServico.FirstAsync(x => x.Id == inbox.OrdemServicoId, ct);
            if (os.Status == StatusOrdemServico.AguardandoAprovacao)
                os.IniciarExecucao();
            inbox.MarkProcessed();
            return;
        }

        if (inbox.MessageType == OrdensMessageTypes.ReservaEstoqueRecusada)
        {
            var payload = envelope.Payload.Deserialize<ReservaEstoqueRecusadaPayload>(MessageJson.Options)
                ?? throw new InvalidOperationException("Payload de reserva recusada invalido.");
            saga.ReservaRecusada(payload.Motivo);
            inbox.MarkProcessed();
            return;
        }

        if (inbox.MessageType == OrdensMessageTypes.ReservaEstoqueLiberada)
        {
            if (saga.Status != StatusSagaOrdemServico.CompensacaoPendente)
            {
                inbox.MarkDeferred("Liberacao recebida antes de compensacao pendente.");
                return;
            }

            var os = await db.OrdensServico.FirstAsync(x => x.Id == inbox.OrdemServicoId, ct);
            if (os.Status is StatusOrdemServico.EmExecucao or StatusOrdemServico.AguardandoAprovacao)
                os.RetornarParaEsperaAposCompensacao();
            saga.Compensada();
            inbox.MarkProcessed();
            return;
        }

        if (inbox.MessageType == OrdensMessageTypes.LiberacaoReservaFalhou)
        {
            var payload = envelope.Payload.Deserialize<LiberacaoReservaFalhouPayload>(MessageJson.Options)
                ?? throw new InvalidOperationException("Payload de liberacao recusada invalido.");
            saga.CompensacaoFalhou(payload.Motivo);
            inbox.MarkProcessed();
            return;
        }

        inbox.MarkDeferred("Evento ainda nao processavel.");
    }
}

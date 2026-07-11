using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

internal sealed class OrdensSqsReceiver(
    IAmazonSQS sqs,
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<SqsMessagingOptions> options,
    ILogger<OrdensSqsReceiver> logger) : SqsBackgroundService(logger)
{
    private string? _queueUrl;

    protected override async Task ExecuteOnce(CancellationToken ct)
    {
        _queueUrl ??= await QueueUrlResolver.Resolve(sqs, options.Value, options.Value.EventsQueueName, ct);
        var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 5,
            WaitTimeSeconds = 5,
            VisibilityTimeout = 20
        }, ct);

        foreach (var message in response.Messages ?? [])
        {
            MessageEnvelope envelope;
            try
            {
                envelope = MessageJson.ParseAndValidate(message.Body);
                if (envelope.MessageType is not (OrdensMessageTypes.EstoqueReservado or OrdensMessageTypes.ReservaEstoqueRecusada or OrdensMessageTypes.ReservaEstoqueLiberada or OrdensMessageTypes.LiberacaoReservaFalhou))
                    throw new InvalidOperationException("Evento desconhecido.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Mensagem invalida recebida antes da Inbox. Sem ACK para redrive nativo.");
                continue;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrdensServicoDbContext>();
            if (!await db.InboxMessages.AnyAsync(x => x.MessageId == envelope.MessageId, ct))
            {
                db.InboxMessages.Add(new InboxMessage(envelope.MessageId, envelope.MessageType, envelope.OrdemServicoId, envelope.CorrelationId, message.Body));
                await db.SaveChangesAsync(ct);
            }

            await sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, ct);
        }
    }
}

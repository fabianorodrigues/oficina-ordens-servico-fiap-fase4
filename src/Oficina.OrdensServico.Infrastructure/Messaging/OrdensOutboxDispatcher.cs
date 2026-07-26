using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oficina.OrdensServico.Infrastructure.Observability;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

internal sealed class OrdensOutboxDispatcher(
    IAmazonSQS sqs,
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<SqsMessagingOptions> options,
    OficinaBusinessMetrics metrics,
    ILogger<OrdensOutboxDispatcher> logger) : SqsBackgroundService(logger)
{
    private string? _queueUrl;

    protected override async Task ExecuteOnce(CancellationToken ct)
    {
        _queueUrl ??= await QueueUrlResolver.Resolve(sqs, options.Value, options.Value.CommandsQueueName, ct);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdensServicoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages
            .Where(x => x.PublishedAtUtc == null && (x.LockedUntilUtc == null || x.LockedUntilUtc < now))
            .OrderBy(x => x.CreatedAtUtc)
            .Take(10)
            .ToListAsync(ct);

        if (messages.Count == 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return;
        }

        foreach (var message in messages)
            message.Claim(now.AddSeconds(30));
        await db.SaveChangesAsync(ct);

        foreach (var message in messages)
        {
            // O span de envio ao SQS e criado pela instrumentacao AWS dentro
            // deste escopo. Aqui so existe o span de despacho, cujo parent vem
            // do envelope e liga a publicacao a origem do fluxo.
            using var activity = MessagingTelemetry.StartOutboxDispatch(message, options.Value.CommandsQueueName);
            try
            {
                await sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MessageBody = message.Body,
                    MessageGroupId = message.OrdemServicoId.ToString(),
                    MessageDeduplicationId = message.MessageId.ToString(),
                    MessageAttributes = MessagingTelemetry.BusinessAttributes(message)
                }, ct);
                message.MarkPublished();
                MessagingTelemetry.SetResult(activity, "published");
            }
            catch (Exception ex)
            {
                MessagingTelemetry.SetFailure(activity, ex);
                metrics.FalhaIntegracao(OficinaTelemetry.Integrations.Sqs, OficinaTelemetry.Operations.Publish);
                metrics.FalhaProcessamento(OficinaTelemetry.Stages.Outbox, OficinaTelemetry.Reasons.SqsPublishFailed);
                logger.LogError(ex, "Falha ao publicar Outbox {MessageId}.", message.MessageId);
                message.MarkFailed(ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(message.Attempts * 2, 30)), ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

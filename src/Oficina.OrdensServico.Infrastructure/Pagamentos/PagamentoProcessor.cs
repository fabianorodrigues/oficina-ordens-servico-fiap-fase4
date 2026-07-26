using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oficina.OrdensServico.Infrastructure.Messaging;
using Oficina.OrdensServico.Infrastructure.Observability;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public sealed class PagamentoProcessor(
    IServiceScopeFactory scopes,
    IPagamentoGateway gateway,
    OficinaBusinessMetrics metrics,
    ILogger<PagamentoProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteOnce(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Processador de pagamento falhou.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task ExecuteOnce(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdensServicoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var pagamento = await db.Pagamentos
            .Where(x => x.Status == StatusPagamentoOrdem.Pendente &&
                (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now) &&
                (x.LockedUntilUtc == null || x.LockedUntilUtc < now))
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (pagamento is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return;
        }

        var workerId = Environment.MachineName + ":" + Guid.NewGuid().ToString("N");
        pagamento.Claim(workerId, now.AddSeconds(30));
        await db.SaveChangesAsync(ct);

        var orcamento = await db.Orcamentos.Include(x => x.ItensMaterial).FirstAsync(x => x.OrdemServicoId == pagamento.OrdemServicoId, ct);
        var correlationId = $"pagamento-{pagamento.OrdemServicoId}";

        // Sem esta Activity o processamento roda sem contexto nenhum, e tanto as
        // chamadas SQL quanto o envelope criado aqui ficariam orfaos no trace.
        using var activity = OficinaTelemetry.ActivitySource.StartActivity(
            OficinaTelemetry.PagamentoActivity,
            ActivityKind.Internal);
        activity?.SetTag(OficinaTelemetry.Attributes.OrdemId, pagamento.OrdemServicoId.ToString());
        activity?.SetTag(OficinaTelemetry.Attributes.CorrelationId, correlationId);

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["OrdemServicoId"] = pagamento.OrdemServicoId
        });

        var transitions = new SagaTransitionBuffer();

        try
        {
            var result = await gateway.Processar(new PagamentoGatewayRequest(pagamento.OrdemServicoId, orcamento.ValorTotal, pagamento.ChaveIdempotencia, correlationId), ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var saga = await db.SagasOrdensServico.FirstAsync(x => x.OrdemServicoId == pagamento.OrdemServicoId, ct);
            activity?.SetTag(OficinaTelemetry.Attributes.SagaPreviousState, saga.Status.ToString());
            if (result.Status == ResultadoPagamentoStatus.Aprovado)
            {
                pagamento.MarcarAprovado(result.PagamentoExternoId ?? Guid.NewGuid().ToString());
                var previousState = saga.Status;
                var previousStateEnteredAtUtc = saga.UpdatedAtUtc;
                saga.PagamentoAprovado();
                transitions.Record(previousState, saga.Status, "approved", previousStateEnteredAtUtc);
                db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, pagamento.OrdemServicoId, previousState, saga.Status, "PagamentoAprovado", null, "Pagamento mock aprovado."));
                previousState = saga.Status;
                previousStateEnteredAtUtc = saga.UpdatedAtUtc;
                saga.ReservaPendente();
                transitions.Record(previousState, saga.Status, "reservation_requested", previousStateEnteredAtUtc);
                db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, pagamento.OrdemServicoId, previousState, saga.Status, "ReservaSolicitada", null, "Comando de reserva criado no Outbox."));
                if (!await db.OutboxMessages.AnyAsync(x => x.OrdemServicoId == pagamento.OrdemServicoId && x.MessageType == OrdensMessageTypes.ReservarEstoque, ct))
                    db.OutboxMessages.Add(FluxoDistribuidoOrdens.CriarReserva(orcamento, correlationId, null));
            }
            else if (result.Status == ResultadoPagamentoStatus.Recusado)
            {
                // Recusado e resultado valido de negocio, nao falha de
                // integracao: nao entra em oficina.integration.failures.
                pagamento.MarcarRecusado(result.PagamentoExternoId, result.Motivo ?? "Pagamento recusado.");
                db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, pagamento.OrdemServicoId, saga.Status, saga.Status, "PagamentoRecusado", null, "Pagamento mock recusado."));
            }
            else
            {
                pagamento.Reagendar("Pagamento pendente.");
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            transitions.Flush(metrics);
            activity?.SetTag(OficinaTelemetry.Attributes.ProcessingResult, result.Status.ToString().ToLowerInvariant());
            activity?.SetTag(OficinaTelemetry.Attributes.SagaCurrentState, saga.Status.ToString());
        }
        catch (HttpRequestException ex)
        {
            transitions.Discard();
            activity?.SetTag(OficinaTelemetry.Attributes.ProcessingResult, "failed");
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            metrics.FalhaIntegracao(OficinaTelemetry.Integrations.PaymentMock, OficinaTelemetry.Operations.Process);
            metrics.FalhaProcessamento(OficinaTelemetry.Stages.Payment, OficinaTelemetry.Reasons.PaymentGatewayFailed);
            pagamento.Reagendar(ex.Message);
            if (pagamento.AttemptCount >= 5)
            {
                pagamento.MarcarFalhaFinal("Falha transitoria esgotada no provedor de pagamento.");
                metrics.FalhaProcessamento(OficinaTelemetry.Stages.Payment, OficinaTelemetry.Reasons.PaymentAttemptsExhausted);
            }

            await db.SaveChangesAsync(ct);
        }
    }
}

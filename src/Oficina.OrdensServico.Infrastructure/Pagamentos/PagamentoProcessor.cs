using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oficina.OrdensServico.Infrastructure.Messaging;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public sealed class PagamentoProcessor(
    IServiceScopeFactory scopes,
    IPagamentoGateway gateway,
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
        try
        {
            var result = await gateway.Processar(new PagamentoGatewayRequest(pagamento.OrdemServicoId, orcamento.ValorTotal, pagamento.ChaveIdempotencia, correlationId), ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var saga = await db.SagasOrdensServico.FirstAsync(x => x.OrdemServicoId == pagamento.OrdemServicoId, ct);
            if (result.Status == ResultadoPagamentoStatus.Aprovado)
            {
                pagamento.MarcarAprovado(result.PagamentoExternoId ?? Guid.NewGuid().ToString());
                var previousState = saga.Status;
                saga.PagamentoAprovado();
                db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, pagamento.OrdemServicoId, previousState, saga.Status, "PagamentoAprovado", null, "Pagamento mock aprovado."));
                previousState = saga.Status;
                saga.ReservaPendente();
                db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, pagamento.OrdemServicoId, previousState, saga.Status, "ReservaSolicitada", null, "Comando de reserva criado no Outbox."));
                if (!await db.OutboxMessages.AnyAsync(x => x.OrdemServicoId == pagamento.OrdemServicoId && x.MessageType == OrdensMessageTypes.ReservarEstoque, ct))
                    db.OutboxMessages.Add(FluxoDistribuidoOrdens.CriarReserva(orcamento, correlationId, null));
            }
            else if (result.Status == ResultadoPagamentoStatus.Recusado)
            {
                pagamento.MarcarRecusado(result.PagamentoExternoId, result.Motivo ?? "Pagamento recusado.");
                db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, pagamento.OrdemServicoId, saga.Status, saga.Status, "PagamentoRecusado", null, "Pagamento mock recusado."));
            }
            else
            {
                pagamento.Reagendar("Pagamento pendente.");
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            pagamento.Reagendar(ex.Message);
            if (pagamento.AttemptCount >= 5)
                pagamento.MarcarFalhaFinal("Falha transitoria esgotada no provedor de pagamento.");
            await db.SaveChangesAsync(ct);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Application.Shared;
using Oficina.OrdensServico.Domain.Oficina;
using Oficina.OrdensServico.Infrastructure.Messaging;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public sealed class FluxoDistribuidoOrdens(OrdensServicoDbContext db) : IFluxoDistribuidoOrdens
{
    public async Task IniciarAprovacao(Orcamento orcamento, CancellationToken ct)
    {
        var chave = ChavePagamento(orcamento.OrdemServicoId);
        if (!await db.Pagamentos.AnyAsync(x => x.ChaveIdempotencia == chave, ct))
            db.Pagamentos.Add(new PagamentoOrdem(orcamento.OrdemServicoId, chave));

        if (!await db.SagasOrdensServico.AnyAsync(x => x.OrdemServicoId == orcamento.OrdemServicoId, ct))
            db.SagasOrdensServico.Add(new SagaOrdemServico(orcamento.OrdemServicoId));
    }

    public async Task ForcarCompensacao(Guid ordemServicoId, string correlationId, CancellationToken ct)
    {
        var saga = await db.SagasOrdensServico.FirstOrDefaultAsync(x => x.OrdemServicoId == ordemServicoId, ct)
            ?? throw new OrdensException("Saga nao encontrada.", 404, "saga_inexistente");
        if (saga.ReservaId is null) throw new OrdensException("Reserva inexistente para compensacao.", 409, "reserva_inexistente");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var previousState = saga.Status;
        saga.CompensacaoPendente();
        db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, ordemServicoId, previousState, saga.Status, "CompensacaoSolicitada", null, "Solicitada liberacao de reserva."));
        db.OutboxMessages.Add(CriarLiberacao(ordemServicoId, saga.ReservaId.Value, correlationId, null));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task ReprocessarReserva(Guid ordemServicoId, string correlationId, CancellationToken ct)
    {
        var orcamento = await db.Orcamentos.Include(x => x.ItensMaterial).FirstOrDefaultAsync(x => x.OrdemServicoId == ordemServicoId, ct)
            ?? throw new OrdensException("Orcamento nao encontrado.", 404);
        var saga = await db.SagasOrdensServico.FirstOrDefaultAsync(x => x.OrdemServicoId == ordemServicoId, ct)
            ?? throw new OrdensException("Saga nao encontrada.", 404, "saga_inexistente");
        if (orcamento.Status != StatusOrcamento.Aprovado) throw new OrdensException("Orcamento precisa estar aprovado.", 409);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var previousState = saga.Status;
        saga.ReservaPendente();
        db.SagaSnapshots.Add(new SagaSnapshot(saga.Id, ordemServicoId, previousState, saga.Status, "ReservaReprocessada", null, "Reserva reenfileirada."));
        db.OutboxMessages.Add(CriarReserva(orcamento, correlationId, null, novaTentativa: true));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    internal static string ChavePagamento(Guid ordemServicoId) => $"ordem-servico:{ordemServicoId}:pagamento";

    internal static OutboxMessage CriarReserva(Orcamento orcamento, string correlationId, string? causationId, bool novaTentativa = false)
    {
        var chave = novaTentativa
            ? $"ordem-servico:{orcamento.OrdemServicoId}:reserva:{Guid.NewGuid():N}"
            : $"ordem-servico:{orcamento.OrdemServicoId}:reserva";
        var payload = new ReservarEstoquePayload(
            chave,
            orcamento.ItensMaterial
                .OrderBy(x => x.Tipo)
                .ThenBy(x => x.MaterialId)
                .Select(x => new ReservarEstoqueItemPayload((int)x.Tipo, x.MaterialId, x.Quantidade))
                .ToList());
        var body = MessageJson.Envelope(OrdensMessageTypes.ReservarEstoque, orcamento.OrdemServicoId, correlationId, causationId, payload);
        var envelope = MessageJson.ParseAndValidate(body);
        return new OutboxMessage(envelope.MessageId, OrdensMessageTypes.ReservarEstoque, orcamento.OrdemServicoId, correlationId, causationId, body);
    }

    internal static OutboxMessage CriarLiberacao(Guid ordemServicoId, Guid reservaId, string correlationId, string? causationId)
    {
        var payload = new LiberarReservaEstoquePayload(reservaId);
        var body = MessageJson.Envelope(OrdensMessageTypes.LiberarReservaEstoque, ordemServicoId, correlationId, causationId, payload);
        var envelope = MessageJson.ParseAndValidate(body);
        return new OutboxMessage(envelope.MessageId, OrdensMessageTypes.LiberarReservaEstoque, ordemServicoId, correlationId, causationId, body);
    }
}

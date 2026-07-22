using Oficina.OrdensServico.Domain.Shared;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public enum StatusSagaOrdemServico
{
    NaoIniciada = 1,
    PagamentoPendente = 2,
    PagamentoAprovado = 3,
    ReservaPendente = 4,
    Reservada = 5,
    ReservaRecusada = 6,
    CompensacaoPendente = 7,
    Compensada = 8,
    CompensacaoFalhou = 9,
    Concluida = 10
}

public sealed class SagaOrdemServico : Entidade
{
    private SagaOrdemServico() { }

    public SagaOrdemServico(Guid ordemServicoId)
    {
        if (ordemServicoId == Guid.Empty) throw new ArgumentException("Ordem de servico invalida.");
        OrdemServicoId = ordemServicoId;
        Status = StatusSagaOrdemServico.PagamentoPendente;
        CreatedAtUtc = UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid OrdemServicoId { get; private set; }
    public StatusSagaOrdemServico Status { get; private set; }
    public Guid? ReservaId { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void PagamentoAprovado() => Atualizar(StatusSagaOrdemServico.PagamentoAprovado);
    public void ReservaPendente() => Atualizar(StatusSagaOrdemServico.ReservaPendente);
    public void Reservada(Guid reservaId) { ReservaId = reservaId; Atualizar(StatusSagaOrdemServico.Reservada); }
    public void Concluir() => Atualizar(StatusSagaOrdemServico.Concluida);
    public void ReservaRecusada(string motivo) { LastError = Sanitizar(motivo); Atualizar(StatusSagaOrdemServico.ReservaRecusada); }
    public void CompensacaoPendente() => Atualizar(StatusSagaOrdemServico.CompensacaoPendente);
    public void Compensada() => Atualizar(StatusSagaOrdemServico.Compensada);
    public void CompensacaoFalhou(string motivo) { LastError = Sanitizar(motivo); Atualizar(StatusSagaOrdemServico.CompensacaoFalhou); }

    private void Atualizar(StatusSagaOrdemServico status)
    {
        Status = status;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string Sanitizar(string erro) => erro.Length <= 500 ? erro : erro[..500];
}

public sealed class SagaSnapshot
{
    private SagaSnapshot() { }

    public SagaSnapshot(
        Guid sagaId,
        Guid ordemServicoId,
        StatusSagaOrdemServico previousState,
        StatusSagaOrdemServico newState,
        string eventType,
        string? triggerMessageId,
        string? payloadSummary)
    {
        Id = Guid.NewGuid();
        SagaId = sagaId;
        OrdemServicoId = ordemServicoId;
        PreviousState = previousState;
        NewState = newState;
        EventType = eventType;
        TriggerMessageId = triggerMessageId;
        PayloadSummary = Sanitize(payloadSummary);
        OccurredAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid SagaId { get; private set; }
    public Guid OrdemServicoId { get; private set; }
    public StatusSagaOrdemServico PreviousState { get; private set; }
    public StatusSagaOrdemServico NewState { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string? TriggerMessageId { get; private set; }
    public string? PayloadSummary { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Length <= 1000 ? value : value[..1000];
    }
}

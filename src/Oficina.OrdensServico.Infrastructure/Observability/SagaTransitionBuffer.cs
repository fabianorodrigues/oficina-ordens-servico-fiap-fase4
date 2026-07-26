using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.Infrastructure.Observability;

public readonly record struct SagaTransition(
    StatusSagaOrdemServico PreviousState,
    StatusSagaOrdemServico CurrentState,
    string Result,
    DateTimeOffset PreviousStateEnteredAtUtc,
    DateTimeOffset TransitionAtUtc)
{
    /// <summary>
    /// Duracao no estado anterior, sempre nao negativa.
    /// O instante nunca vem do OccurredAtUtc da mensagem: relogio externo,
    /// atraso de entrega ou mensagem antiga produziriam duracao negativa.
    /// </summary>
    public double DurationSeconds
        => Math.Max(0d, (TransitionAtUtc - PreviousStateEnteredAtUtc).TotalSeconds);
}

/// <summary>
/// Acumula as transicoes durante a transacao e emite as metricas somente depois
/// do commit. Emitir no momento da transicao contaria de novo a cada
/// reprocessamento do Inbox apos rollback.
/// </summary>
public sealed class SagaTransitionBuffer
{
    private readonly List<SagaTransition> _pending = [];

    public IReadOnlyList<SagaTransition> Pending => _pending;

    /// <summary>
    /// Registra a transicao. Chamar sempre com o UpdatedAtUtc capturado ANTES do
    /// mutator da saga: depois dele o valor ja foi sobrescrito.
    /// </summary>
    public void Record(
        StatusSagaOrdemServico previousState,
        StatusSagaOrdemServico currentState,
        string result,
        DateTimeOffset previousStateEnteredAtUtc)
        => _pending.Add(new SagaTransition(
            previousState,
            currentState,
            result,
            previousStateEnteredAtUtc,
            DateTimeOffset.UtcNow));

    public void Flush(OficinaBusinessMetrics metrics)
    {
        foreach (var transition in _pending)
        {
            metrics.Transicao(
                transition.PreviousState.ToString(),
                transition.CurrentState.ToString(),
                transition.Result,
                transition.DurationSeconds);
        }

        _pending.Clear();
    }

    public void Discard() => _pending.Clear();
}

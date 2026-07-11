using Oficina.OrdensServico.Domain.Shared;

namespace Oficina.OrdensServico.Infrastructure.Pagamentos;

public enum StatusPagamentoOrdem
{
    Pendente = 1,
    Aprovado = 2,
    Recusado = 3,
    Falhou = 4,
    Compensado = 5
}

public sealed class PagamentoOrdem : Entidade
{
    private PagamentoOrdem() { }

    public PagamentoOrdem(Guid ordemServicoId, string chaveIdempotencia)
    {
        if (ordemServicoId == Guid.Empty) throw new ArgumentException("Ordem de servico invalida.");
        if (string.IsNullOrWhiteSpace(chaveIdempotencia)) throw new ArgumentException("Chave de idempotencia invalida.");
        OrdemServicoId = ordemServicoId;
        ChaveIdempotencia = chaveIdempotencia;
        Provider = "Mock";
        Status = StatusPagamentoOrdem.Pendente;
        CreatedAtUtc = UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid OrdemServicoId { get; private set; }
    public string? PagamentoExternoId { get; private set; }
    public string ChaveIdempotencia { get; private set; } = string.Empty;
    public string Provider { get; private set; } = "Mock";
    public StatusPagamentoOrdem Status { get; private set; }
    public string OperationType { get; private set; } = "Payment";
    public string? CompensacaoExternaId { get; private set; }
    public DateTimeOffset? CompensatedAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public string? LockedBy { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool PodeProcessar(DateTimeOffset now) =>
        Status == StatusPagamentoOrdem.Pendente &&
        (NextAttemptAtUtc is null || NextAttemptAtUtc <= now) &&
        (LockedUntilUtc is null || LockedUntilUtc < now);

    public void Claim(string workerId, DateTimeOffset lockedUntilUtc)
    {
        AttemptCount++;
        LockedBy = workerId;
        LockedUntilUtc = lockedUntilUtc;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void MarcarAprovado(string pagamentoExternoId)
    {
        Status = StatusPagamentoOrdem.Aprovado;
        Provider = pagamentoExternoId.StartsWith("mock-", StringComparison.OrdinalIgnoreCase) ? "Mock" : "ExternalPaymentApi";
        PagamentoExternoId = pagamentoExternoId;
        LimparLock();
    }

    public void MarcarRecusado(string? pagamentoExternoId, string motivo)
    {
        Status = StatusPagamentoOrdem.Recusado;
        PagamentoExternoId = pagamentoExternoId;
        LastError = Sanitizar(motivo);
        LimparLock();
    }

    public void MarcarCompensado(string compensacaoExternaId)
    {
        if (Status == StatusPagamentoOrdem.Compensado)
            return;

        Status = StatusPagamentoOrdem.Compensado;
        OperationType = "Compensation";
        CompensacaoExternaId = compensacaoExternaId;
        CompensatedAtUtc = DateTimeOffset.UtcNow;
        LimparLock();
    }

    public void Reagendar(string erro)
    {
        LastError = Sanitizar(erro);
        NextAttemptAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(Math.Max(AttemptCount, 1) * 5, 60));
        LimparLock();
    }

    public void MarcarFalhaFinal(string erro)
    {
        Status = StatusPagamentoOrdem.Falhou;
        LastError = Sanitizar(erro);
        LimparLock();
    }

    private void LimparLock()
    {
        LockedBy = null;
        LockedUntilUtc = null;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string Sanitizar(string erro) => erro.Length <= 500 ? erro : erro[..500];
}

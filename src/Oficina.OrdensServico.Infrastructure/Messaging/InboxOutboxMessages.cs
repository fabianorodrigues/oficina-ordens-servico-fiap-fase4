namespace Oficina.OrdensServico.Infrastructure.Messaging;

public enum InboxMessageStatus
{
    Received = 1,
    Processing = 2,
    Processed = 3,
    Deferred = 4,
    DeadLettered = 5
}

public sealed class InboxMessage
{
    private InboxMessage() { }

    public InboxMessage(Guid messageId, string messageType, Guid ordemServicoId, string correlationId, string body)
    {
        MessageId = messageId;
        MessageType = messageType;
        OrdemServicoId = ordemServicoId;
        CorrelationId = correlationId;
        Body = body;
        ReceivedAtUtc = DateTimeOffset.UtcNow;
        Status = InboxMessageStatus.Received;
    }

    public long Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string MessageType { get; private set; } = string.Empty;
    public Guid OrdemServicoId { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public InboxMessageStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset ReceivedAtUtc { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    public string? Error { get; private set; }

    public void Claim(DateTimeOffset lockedUntilUtc) { Status = InboxMessageStatus.Processing; Attempts++; LockedUntilUtc = lockedUntilUtc; }
    public void MarkProcessed() { Status = InboxMessageStatus.Processed; ProcessedAtUtc = DateTimeOffset.UtcNow; LockedUntilUtc = null; Error = null; }
    public void MarkDeferred(string reason) { Status = InboxMessageStatus.Deferred; LockedUntilUtc = DateTimeOffset.UtcNow.AddSeconds(30); Error = reason.Length <= 500 ? reason : reason[..500]; }
    public void MarkFailed(string error, bool deadLetter) { Status = deadLetter ? InboxMessageStatus.DeadLettered : InboxMessageStatus.Received; LockedUntilUtc = null; Error = error.Length <= 500 ? error : error[..500]; }
}

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public OutboxMessage(Guid messageId, string messageType, Guid ordemServicoId, string correlationId, string? causationId, string body)
    {
        MessageId = messageId;
        MessageType = messageType;
        OrdemServicoId = ordemServicoId;
        CorrelationId = correlationId;
        CausationId = causationId;
        Body = body;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public long Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string MessageType { get; private set; } = string.Empty;
    public Guid OrdemServicoId { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? CausationId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public int Attempts { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }
    public string? Error { get; private set; }

    public void Claim(DateTimeOffset lockedUntilUtc) { Attempts++; LockedUntilUtc = lockedUntilUtc; }
    public void MarkPublished() { PublishedAtUtc = DateTimeOffset.UtcNow; LockedUntilUtc = null; Error = null; }
    public void MarkFailed(string error) { LockedUntilUtc = null; Error = error.Length <= 500 ? error : error[..500]; }
}

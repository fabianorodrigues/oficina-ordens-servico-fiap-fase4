using System.Text.Json;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

public sealed record MessageEnvelope(
    Guid MessageId,
    string MessageType,
    int SchemaVersion,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    string? CausationId,
    Guid OrdemServicoId,
    JsonElement Payload);

public static class OrdensMessageTypes
{
    public const string ReservarEstoque = nameof(ReservarEstoque);
    public const string LiberarReservaEstoque = nameof(LiberarReservaEstoque);
    public const string EstoqueReservado = nameof(EstoqueReservado);
    public const string ReservaEstoqueRecusada = nameof(ReservaEstoqueRecusada);
    public const string ReservaEstoqueLiberada = nameof(ReservaEstoqueLiberada);
    public const string LiberacaoReservaFalhou = nameof(LiberacaoReservaFalhou);
}

public sealed record ReservarEstoquePayload(string ChaveOperacao, IReadOnlyList<ReservarEstoqueItemPayload> Itens);
public sealed record ReservarEstoqueItemPayload(int TipoMaterial, Guid MaterialId, int Quantidade);
public sealed record LiberarReservaEstoquePayload(Guid ReservaId, string? ChaveOperacao = null);
public sealed record EstoqueReservadoPayload(Guid ReservaId, bool Duplicada);
public sealed record ReservaEstoqueRecusadaPayload(string Codigo, string Motivo);
public sealed record ReservaEstoqueLiberadaPayload(Guid ReservaId, bool JaLiberada);
public sealed record LiberacaoReservaFalhouPayload(Guid? ReservaId, string Codigo, string Motivo);

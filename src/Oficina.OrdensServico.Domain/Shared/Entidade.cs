namespace Oficina.OrdensServico.Domain.Shared;

public abstract class Entidade
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
}

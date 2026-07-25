namespace Oficina.OrdensServico.Domain.SharedKernel;

public abstract class Entidade
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
}

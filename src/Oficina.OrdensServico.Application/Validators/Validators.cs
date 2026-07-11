using FluentValidation;
using Oficina.OrdensServico.Application.Contracts;

namespace Oficina.OrdensServico.Application.Validators;

public sealed class AbrirOrdemServicoRequestValidator : AbstractValidator<AbrirOrdemServicoRequest>
{
    public AbrirOrdemServicoRequestValidator()
    {
        RuleFor(x => x.Cliente.Documento).NotEmpty();
        RuleFor(x => x.Cliente.Nome).NotEmpty();
        RuleFor(x => x.Veiculo.Placa).NotEmpty();
        RuleFor(x => x.Veiculo.Renavam).NotEmpty();
        RuleForEach(x => x.Itens.Pecas).ChildRules(i => i.RuleFor(x => x.Quantidade).GreaterThan(0));
        RuleForEach(x => x.Itens.Insumos).ChildRules(i => i.RuleFor(x => x.Quantidade).GreaterThan(0));
    }
}

public sealed class RegistrarDiagnosticoRequestValidator : AbstractValidator<RegistrarDiagnosticoRequest>
{
    public RegistrarDiagnosticoRequestValidator()
    {
        RuleFor(x => x.Descricao).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.ServicoIds).NotEmpty();
    }
}

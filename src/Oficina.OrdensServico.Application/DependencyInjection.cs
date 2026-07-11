using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Application.UseCases;

namespace Oficina.OrdensServico.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddOrdensApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddScoped<OrdensUseCases>();
        services.AddSingleton<INotificadorCliente, LogOnlyNotificadorCliente>();
        services.AddOptions<DistributedFlowOptions>().BindConfiguration("DistributedFlow");
        return services;
    }
}

internal sealed class LogOnlyNotificadorCliente : INotificadorCliente
{
    public Task NotificarOrcamentoCriado(Guid orcamentoId, Guid ordemServicoId, CancellationToken ct) => Task.CompletedTask;
    public Task NotificarOrcamentoRecusado(Guid orcamentoId, Guid ordemServicoId, CancellationToken ct) => Task.CompletedTask;
}

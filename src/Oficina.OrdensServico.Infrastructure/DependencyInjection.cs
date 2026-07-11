using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Infrastructure.Http;
using Oficina.OrdensServico.Infrastructure.Messaging;
using Oficina.OrdensServico.Infrastructure.Pagamentos;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrdensInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("OficinaOrdensServicoDb")
            ?? "Server=(localdb)\\mssqllocaldb;Database=OficinaOrdensServicoDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";
        services.AddDbContext<OrdensServicoDbContext>(o => o
            .UseSqlServer(cs)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddScoped<IOrdensServicoRepository, OrdensServicoRepository>();
        services.AddHttpContextAccessor();
        services.AddTransient<CorrelationHeaderHandler>();
        services.AddHttpClient<ICadastroClient, CadastroClient>(c =>
        {
            c.BaseAddress = new Uri(configuration["Integrations:Cadastro:BaseUrl"] ?? "http://localhost:5101");
            c.Timeout = TimeSpan.FromSeconds(configuration.GetValue("Integrations:Cadastro:TimeoutSeconds", 5));
        }).AddHttpMessageHandler<CorrelationHeaderHandler>();
        services.AddHttpClient<IEstoqueClient, EstoqueClient>(c =>
        {
            c.BaseAddress = new Uri(configuration["Integrations:Estoque:BaseUrl"] ?? "http://localhost:5102");
            c.Timeout = TimeSpan.FromSeconds(configuration.GetValue("Integrations:Estoque:TimeoutSeconds", 5));
        }).AddHttpMessageHandler<CorrelationHeaderHandler>();
        services.AddScoped<IFluxoDistribuidoOrdens, FluxoDistribuidoOrdens>();
        if (string.Equals(configuration["Payments:Mode"], "Mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPagamentoGateway, MockPagamentoGateway>();
        }
        else
        {
            services.AddHttpClient<IPagamentoGateway, ApiPagamentoGateway>(c =>
            {
                c.BaseAddress = new Uri(configuration["Payments:BaseUrl"] ?? "http://localhost:5110");
                c.Timeout = TimeSpan.FromSeconds(configuration.GetValue("Payments:TimeoutSeconds", 5));
            });
        }
        services.AddHostedService<PagamentoProcessor>();
        services.AddOrdensMessaging(configuration);
        return services;
    }
}

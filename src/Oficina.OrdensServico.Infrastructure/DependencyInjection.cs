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
        services.AddSingleton(configuration);
        var isProduction = string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], "Production", StringComparison.OrdinalIgnoreCase);
        var cs = configuration.GetConnectionString("DefaultConnection")
            ?? configuration.GetConnectionString("OficinaOrdensServicoDb");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (isProduction)
                throw new InvalidOperationException("A connection string obrigatoria nao foi configurada.");

            cs = "Server=(localdb)\\mssqllocaldb;Database=OficinaOrdensServicoDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";
        }
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
        ValidatePayments(configuration);

        services.AddSingleton<IExternalPaymentContractMapper, PendingExternalPaymentContractMapper>();
        services.AddSingleton<IPaymentWebhookAuthenticator, PendingPaymentWebhookAuthenticator>();
        services.AddScoped<IPaymentWebhookHandler, PaymentWebhookHandler>();

        if (UseMock(configuration))
        {
            services.AddSingleton<IPagamentoGateway, MockPagamentoGateway>();
        }
        else
        {
            services.AddHttpClient<IPagamentoGateway, ExternalPaymentApiGateway>("ExternalPaymentApi", c =>
            {
                c.BaseAddress = new Uri(configuration["Payments:BaseUrl"]!);
                c.Timeout = TimeSpan.FromSeconds(configuration.GetValue("Payments:TimeoutSeconds", 5));
            });
        }
        services.AddHostedService<PagamentoProcessor>();
        services.AddOrdensMessaging(configuration);
        return services;
    }

    private static bool UseMock(IConfiguration configuration) =>
        configuration.GetValue("Payments:UseMock", false)
        || string.Equals(configuration["PAYMENTS_USE_MOCK"], "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(configuration["Payments:Mode"], "Mock", StringComparison.OrdinalIgnoreCase);

    private static void ValidatePayments(IConfiguration configuration)
    {
        var behavior = configuration["Payments:MockBehavior"] ?? configuration["Payments:Scenario"] ?? "Approved";
        if (!new[] { "Approved", "Aprovado", "Rejected", "Recusado", "Pending", "Pendente" }
            .Contains(behavior, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Payments:MockBehavior invalido.");

        if (UseMock(configuration))
            return;

        if (!configuration.GetValue("Payments:ExternalApiEnabled", false))
            throw new InvalidOperationException("Payments:ExternalApiEnabled=true e obrigatorio quando UseMock=false.");
        if (!string.Equals(configuration["Payments:ContractStatus"], "Ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Payments:ContractStatus=Ready e obrigatorio quando UseMock=false.");
        if (string.IsNullOrWhiteSpace(configuration["Payments:BaseUrl"]))
            throw new InvalidOperationException("Payments:BaseUrl e obrigatorio quando UseMock=false.");
        if (string.IsNullOrWhiteSpace(configuration["Payments:SubmitPath"]))
            throw new InvalidOperationException("Payments:SubmitPath e obrigatorio quando UseMock=false.");
        if (!configuration.GetValue("Payments:ExternalWebhookEnabled", false))
            throw new InvalidOperationException("Payments:ExternalWebhookEnabled=true e obrigatorio quando UseMock=false.");
        if (string.IsNullOrWhiteSpace(configuration["Application:PublicBaseUrl"]))
            throw new InvalidOperationException("Application:PublicBaseUrl e obrigatorio quando UseMock=false.");

        throw new InvalidOperationException("Contrato externo de pagamentos pendente. Registre mapper e autenticador concretos antes de UseMock=false.");
    }
}

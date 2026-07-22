using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Oficina.OrdensServico.Api;

internal static class ProductionStartupValidation
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsProduction())
            return;

        Required(configuration.GetConnectionString("DefaultConnection")
            ?? configuration.GetConnectionString("OficinaOrdensServicoDb"), "A connection string obrigatoria nao foi configurada.");
        Required(configuration["Messaging:Sqs:Region"], "A regiao AWS obrigatoria nao foi configurada.");
        Required(configuration["Messaging:Sqs:CommandsQueueUrl"], "A URL da fila de comandos nao foi configurada.");
        Required(configuration["Messaging:Sqs:CommandsDlqQueueUrl"], "A URL da DLQ de comandos nao foi configurada.");
        Required(configuration["Messaging:Sqs:EventsQueueUrl"], "A URL da fila de eventos nao foi configurada.");
        Required(configuration["Messaging:Sqs:EventsDlqQueueUrl"], "A URL da DLQ de eventos nao foi configurada.");
        Required(configuration["Integrations:Cadastro:BaseUrl"], "A URL interna do Cadastro nao foi configurada.");
        Required(configuration["Integrations:Estoque:BaseUrl"], "A URL interna do Estoque nao foi configurada.");

        if (configuration.GetValue("Database:ApplyMigrations", false))
            throw new InvalidOperationException("Database__ApplyMigrations=true so pode ser usado em Development.");
        if (string.Equals(configuration["Authentication:Mode"], "Development", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Authentication Development nao pode ser usado em Production.");
        if (!configuration.GetValue("Payments:UseMock", false) &&
            !string.Equals(configuration["PAYMENTS_USE_MOCK"], "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuration["Payments:Mode"], "Mock", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Payments__UseMock=true deve estar habilitado nesta versao.");
        if (!string.Equals(configuration["Payments:MockBehavior"], "Approved", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuration["Payments:Scenario"], "aprovado", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Payments__MockBehavior=Approved deve ser usado nesta versao.");
        if (configuration.GetValue("Payments:ExternalApiEnabled", false))
            throw new InvalidOperationException("Payments__ExternalApiEnabled deve permanecer false enquanto o contrato esta pendente.");
        if (configuration.GetValue("Payments:ExternalWebhookEnabled", false))
            throw new InvalidOperationException("Payments__ExternalWebhookEnabled deve permanecer false enquanto o contrato esta pendente.");
        if (!string.Equals(configuration["Payments:ContractStatus"], "Pending", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Payments__ContractStatus=Pending deve ser usado nesta versao.");
        if (configuration.GetValue("Messaging:Sqs:ConsumerConcurrency", 1) != 1)
            throw new InvalidOperationException("Consumer concurrency deve ser igual a 1.");
        if (configuration.GetValue("Messaging:Sqs:MaxMessagesPerReceive", 1) != 1)
            throw new InvalidOperationException("Max messages deve ser igual a 1.");
        if (configuration.GetValue("Messaging:Sqs:WaitTimeSeconds", 20) <= 0)
            throw new InvalidOperationException("WaitTimeSeconds invalido.");
        if (configuration.GetValue("Messaging:Sqs:VisibilityTimeoutSeconds", 60) <= 0)
            throw new InvalidOperationException("VisibilityTimeoutSeconds invalido.");
    }

    private static void Required(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(message);
    }
}

using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.Console;

namespace Oficina.OrdensServico.Api.Observability;

internal static class LoggingRegistration
{
    /// <summary>
    /// Substitui AddJsonConsole pelo formatter proprio.
    /// Os logs continuam saindo somente por stdout: quem envia para o New Relic
    /// e o receiver filelog do Collector. A aplicacao nao exporta log por OTLP,
    /// para nao entregar o mesmo registro por dois caminhos.
    /// </summary>
    public static ILoggingBuilder AddOficinaJsonConsole(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        string defaultServiceName)
    {
        var resource = OficinaTelemetryResource.Resolve(configuration, defaultServiceName);

        logging.AddConsole(options => options.FormatterName = OficinaJsonConsoleFormatter.FormatterName);
        logging.AddConsoleFormatter<OficinaJsonConsoleFormatter, OficinaJsonConsoleFormatterOptions>();
        logging.Services.Configure<OficinaJsonConsoleFormatterOptions>(options =>
        {
            // IncludeScopes e obrigatorio: sem ele o scopeProvider chega nulo no
            // formatter e o correlationId do middleware nunca alcanca o stdout.
            options.IncludeScopes = true;
            options.ServiceName = resource.ServiceName;
            options.ServiceVersion = resource.ServiceVersion;
            options.DeploymentEnvironment = resource.DeploymentEnvironment;
        });

        return logging;
    }
}

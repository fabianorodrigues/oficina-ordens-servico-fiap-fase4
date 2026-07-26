using Oficina.OrdensServico.Infrastructure.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Oficina.OrdensServico.Api.Observability;

internal static class OpenTelemetryRegistration
{
    /// <summary>
    /// Registro fail-open da telemetria.
    /// OTEL_EXPORTER_OTLP_ENDPOINT e a unica chave aceita para o gateway OTLP.
    /// Se o endpoint estiver ausente em execucao local, nada de OpenTelemetry e
    /// registrado.
    /// </summary>
    public static IServiceCollection AddOpenTelemetryFailOpen(
        this IServiceCollection services,
        IConfiguration configuration,
        ILoggingBuilder logging,
        string serviceName)
    {
        try
        {
            var otlpEndpoint = ResolveOtlpEndpoint(configuration);
            if (string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                return services;
            }

            var resource = OficinaTelemetryResource.Resolve(configuration, serviceName);

            services.AddOpenTelemetry()
                .ConfigureResource(builder => builder.AddService(
                    serviceName: resource.ServiceName,
                    serviceVersion: resource.ServiceVersion))
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        // /ready e chamado pelo kubelet a cada 10s. /health
                        // permanece rastreado porque a validacao remota depende
                        // dele para provar o caminho ponta a ponta.
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase);
                    });
                    tracing.AddHttpClientInstrumentation();
                    tracing.AddSqlClientInstrumentation();
                    // A instrumentacao AWS cria o span de envio ao SQS e injeta
                    // traceparent/tracestate nos MessageAttributes. Nao existe
                    // injecao manual: os dois mecanismos somados duplicariam o
                    // span de publicacao.
                    tracing.AddAWSInstrumentation();
                    tracing.AddSource(OficinaTelemetry.ActivitySourceName);

                    tracing.AddOtlpExporter(options => ConfigureExporter(options, otlpEndpoint));
                })
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation();
                    metrics.AddHttpClientInstrumentation();
                    metrics.AddRuntimeInstrumentation();
                    // Meter de negocio: oficina.os.* e oficina.integration.*.
                    metrics.AddMeter(OficinaTelemetry.MeterName);

                    metrics.AddOtlpExporter((exporter, _) => ConfigureExporter(exporter, otlpEndpoint));
                });
        }
        catch (Exception ex)
        {
            logging.Services.AddSingleton<IStartupFilter>(_ => new OpenTelemetryStartupWarningFilter(ex));
        }

        return services;
    }

    private static string? ResolveOtlpEndpoint(IConfiguration configuration)
        => Normalize(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Telemetria nunca pode bloquear request, consumo ou health check: o timeout
    // curto garante que um Collector indisponivel falhe rapido e em silencio.
    private static void ConfigureExporter(OpenTelemetry.Exporter.OtlpExporterOptions options, string endpoint)
    {
        options.Endpoint = new Uri(endpoint);
        options.TimeoutMilliseconds = 5000;
    }
}

internal sealed class OpenTelemetryStartupWarningFilter(Exception exception) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("OpenTelemetry");
            logger.LogWarning(exception, "OpenTelemetry disabled after fail-open startup error.");
            next(app);
        };
}

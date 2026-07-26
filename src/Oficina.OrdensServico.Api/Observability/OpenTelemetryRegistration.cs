using Oficina.OrdensServico.Infrastructure.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Oficina.OrdensServico.Api.Observability;

internal static class OpenTelemetryRegistration
{
    /// <summary>
    /// Registro fail-open da telemetria.
    /// Os dois gates originais permanecem: OpenTelemetry:Enabled desliga tudo e
    /// OpenTelemetry:OtlpEndpoint decide se algum exporter e registrado. O
    /// endpoint efetivo continua vindo de OTEL_EXPORTER_OTLP_ENDPOINT, lido pelo
    /// proprio SDK.
    /// </summary>
    public static IServiceCollection AddOpenTelemetryFailOpen(
        this IServiceCollection services,
        IConfiguration configuration,
        ILoggingBuilder logging,
        string serviceName)
    {
        try
        {
            if (!configuration.GetValue("OpenTelemetry:Enabled", true))
            {
                return services;
            }

            var resource = OficinaTelemetryResource.Resolve(configuration, serviceName);
            var exportEnabled = !string.IsNullOrWhiteSpace(configuration["OpenTelemetry:OtlpEndpoint"]);

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

                    if (exportEnabled)
                    {
                        tracing.AddOtlpExporter(ConfigureExporterTimeout);
                    }
                })
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation();
                    metrics.AddHttpClientInstrumentation();
                    metrics.AddRuntimeInstrumentation();
                    // Meter de negocio: oficina.os.* e oficina.integration.*.
                    metrics.AddMeter(OficinaTelemetry.MeterName);

                    if (exportEnabled)
                    {
                        metrics.AddOtlpExporter((exporter, _) => ConfigureExporterTimeout(exporter));
                    }
                });
        }
        catch (Exception ex)
        {
            logging.Services.AddSingleton<IStartupFilter>(_ => new OpenTelemetryStartupWarningFilter(ex));
        }

        return services;
    }

    // Telemetria nunca pode bloquear request, consumo ou health check: o timeout
    // curto garante que um Collector indisponivel falhe rapido e em silencio.
    private static void ConfigureExporterTimeout(OpenTelemetry.Exporter.OtlpExporterOptions options)
        => options.TimeoutMilliseconds = 5000;
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

using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Oficina.OrdensServico.Api.Observability;

internal static class OpenTelemetryRegistration
{
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

            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(serviceName))
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation();

                    var endpoint = configuration["OpenTelemetry:OtlpEndpoint"];
                    if (!string.IsNullOrWhiteSpace(endpoint))
                    {
                        tracing.AddOtlpExporter();
                    }
                });
        }
        catch (Exception ex)
        {
            logging.Services.AddSingleton<IStartupFilter>(_ => new OpenTelemetryStartupWarningFilter(ex));
        }

        return services;
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

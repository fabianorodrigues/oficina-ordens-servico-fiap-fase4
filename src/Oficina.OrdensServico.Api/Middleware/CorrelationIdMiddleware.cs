using System.Diagnostics;
using Oficina.OrdensServico.Infrastructure.Observability;

namespace Oficina.OrdensServico.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task Invoke(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        // Sem esta tag o span da requisicao nao e localizavel pelo
        // correlationId, e a validacao automatizada por Span nao funciona.
        Activity.Current?.SetTag(OficinaTelemetry.Attributes.CorrelationId, correlationId);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            var stopwatch = Stopwatch.StartNew();
            Exception? failure = null;

            try
            {
                await next(context);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                stopwatch.Stop();
                // O log vai em finally, e nao depois de next: requisicao que
                // estoura excecao e justamente a que mais precisa aparecer
                // correlacionada. Os logs de request do hosting sao emitidos
                // fora deste middleware e nao carregam o scope.
                var level = IsReadinessProbe(context) ? LogLevel.Debug : LogLevel.Information;
                logger.Log(
                    level,
                    "HTTP {Method} {Route} completed with {StatusCode} in {ElapsedMs} ms. Failed: {Failed}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    failure is not null);
            }
        }
    }

    private static bool IsReadinessProbe(HttpContext context)
        => context.Request.Path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase);

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value.ToString();
        }

        return Guid.NewGuid().ToString("D");
    }
}

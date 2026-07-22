namespace Oficina.OrdensServico.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task Invoke(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            await next(context);
        }
    }

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

using System.Net;
using FluentValidation;
using Oficina.OrdensServico.Application.Shared;

namespace Oficina.OrdensServico.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString()
                ?? context.TraceIdentifier;

            if (ex is OrdensException ordens)
            {
                context.Response.StatusCode = ordens.StatusCode;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new { title = ordens.Message, status = ordens.StatusCode, code = ordens.Code, correlationId });
                return;
            }
            if (ex is ValidationException validation)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new { title = "Validation Error", status = 400, errors = validation.Errors.Select(e => new { e.PropertyName, e.ErrorMessage }), correlationId });
                return;
            }
            logger.LogError(ex, "Unhandled exception. CorrelationId: {CorrelationId}", correlationId);

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/problem+json";

            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://httpstatuses.com/500",
                title = "Internal Server Error",
                status = StatusCodes.Status500InternalServerError,
                correlationId
            });
        }
    }
}

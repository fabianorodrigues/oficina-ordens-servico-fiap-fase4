using Microsoft.AspNetCore.Http;

namespace Oficina.OrdensServico.Infrastructure.Http;

public sealed class CorrelationHeaderHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    private static readonly string[] DevHeaders = ["X-Dev-Role", "X-Dev-Cpf", "X-Dev-ClienteId", "X-Dev-FuncionarioId"];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var http = accessor.HttpContext;
        if (http is not null)
        {
            if (http.Request.Headers.TryGetValue("X-Correlation-Id", out var correlation))
                request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlation.ToArray());
            foreach (var header in DevHeaders)
                if (http.Request.Headers.TryGetValue(header, out var value))
                    request.Headers.TryAddWithoutValidation(header, value.ToArray());
        }
        return base.SendAsync(request, cancellationToken);
    }
}

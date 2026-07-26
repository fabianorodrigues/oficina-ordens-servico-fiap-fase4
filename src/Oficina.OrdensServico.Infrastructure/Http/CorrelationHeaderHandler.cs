using Microsoft.AspNetCore.Http;

namespace Oficina.OrdensServico.Infrastructure.Http;

public sealed class CorrelationHeaderHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    private static readonly string[] DevHeaders = ["X-Dev-Role", "X-Dev-Cpf", "X-Dev-ClienteId", "X-Dev-FuncionarioId"];

    // Identidade validada pelo authorizer da API Gateway. Precisa ser repassada nas chamadas
    // internas, senao o servico chamado recebe a requisicao sem identidade e a rejeita.
    private static readonly string[] IdentityHeaders =
        ["x-oficina-user-id", "x-oficina-user-cpf", "x-oficina-user-role", "x-oficina-user-name"];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var http = accessor.HttpContext;
        if (http is not null)
        {
            // Items tem precedencia sobre o header de entrada: quando o chamador
            // nao envia X-Correlation-Id, o middleware gera um e o guarda ali.
            // Ler somente o header deixaria o id gerado sem propagacao.
            var correlationId = ResolveCorrelationId(http);
            if (!string.IsNullOrWhiteSpace(correlationId))
                request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
            foreach (var header in IdentityHeaders)
                if (http.Request.Headers.TryGetValue(header, out var identity))
                    request.Headers.TryAddWithoutValidation(header, identity.ToArray());
            foreach (var header in DevHeaders)
                if (http.Request.Headers.TryGetValue(header, out var value))
                    request.Headers.TryAddWithoutValidation(header, value.ToArray());
        }
        return base.SendAsync(request, cancellationToken);
    }

    private static string? ResolveCorrelationId(HttpContext http)
    {
        if (http.Items.TryGetValue("X-Correlation-Id", out var fromItems) && fromItems is string value &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return http.Request.Headers.TryGetValue("X-Correlation-Id", out var header) && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : null;
    }
}

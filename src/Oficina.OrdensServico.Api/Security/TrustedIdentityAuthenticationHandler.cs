using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Oficina.OrdensServico.Api.Security;

public static class TrustedIdentityAuthenticationDefaults
{
    public const string Scheme = "TrustedIdentity";

    public const string UserIdHeader = "x-oficina-user-id";
    public const string UserCpfHeader = "x-oficina-user-cpf";
    public const string UserRoleHeader = "x-oficina-user-role";
    public const string UserNameHeader = "x-oficina-user-name";
}

/// <summary>
/// Materializa a identidade validada pelo authorizer da API Gateway, recebida como cabecalhos.
/// Depende do acesso direto ao balanceador permanecer restrito ao VPC Link: os cabeçalhos
/// so sao confiaveis porque nenhum chamador externo alcanca o servico sem passar pela borda.
/// </summary>
public sealed class TrustedIdentityAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly HashSet<string> ValidRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        PerfisAcesso.Cliente,
        PerfisAcesso.Funcionario,
        PerfisAcesso.Admin
    };

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Header(TrustedIdentityAuthenticationDefaults.UserIdHeader);
        var role = Header(TrustedIdentityAuthenticationDefaults.UserRoleHeader);

        if (string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult(AuthenticateResult.Fail("Identidade sem identificador."));
        }

        if (string.IsNullOrWhiteSpace(role) || !ValidRoles.Contains(role))
        {
            return Task.FromResult(AuthenticateResult.Fail("Identidade com perfil invalido."));
        }

        var normalizedRole = NormalizeRole(role);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Role, normalizedRole)
        };

        AddOptionalClaim(claims, "cpf", TrustedIdentityAuthenticationDefaults.UserCpfHeader);
        AddOptionalClaim(claims, ClaimTypes.Name, TrustedIdentityAuthenticationDefaults.UserNameHeader);

        if (Guid.TryParse(userId, out var subject))
        {
            var claimType = string.Equals(normalizedRole, PerfisAcesso.Cliente, StringComparison.OrdinalIgnoreCase)
                ? "clienteId"
                : "funcionarioId";

            claims.Add(new Claim(claimType, subject.ToString("D")));
        }

        var identity = new ClaimsIdentity(claims, TrustedIdentityAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, TrustedIdentityAuthenticationDefaults.Scheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? Header(string name) => Context.Request.Headers[name].FirstOrDefault();

    private void AddOptionalClaim(List<Claim> claims, string claimType, string headerName)
    {
        var value = Header(headerName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new Claim(claimType, value));
        }
    }

    private static string NormalizeRole(string role)
        => ValidRoles.First(validRole => string.Equals(validRole, role, StringComparison.OrdinalIgnoreCase));
}

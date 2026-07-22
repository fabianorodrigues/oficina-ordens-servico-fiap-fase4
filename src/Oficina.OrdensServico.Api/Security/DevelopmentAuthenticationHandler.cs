using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Oficina.OrdensServico.Api.Security;

public static class DevelopmentAuthenticationDefaults
{
    public const string Scheme = "Development";
}

public static class DevelopmentAuthenticationRegistration
{
    public static IServiceCollection AddOficinaAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var mode = configuration["Authentication:Mode"];
        var isDevelopmentMode = string.Equals(mode, DevelopmentAuthenticationDefaults.Scheme, StringComparison.OrdinalIgnoreCase);

        if (isDevelopmentMode && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Authentication__Mode=Development is allowed only when ASPNETCORE_ENVIRONMENT=Development.");
        }

        if (isDevelopmentMode)
        {
            services
                .AddAuthentication(DevelopmentAuthenticationDefaults.Scheme)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
                    DevelopmentAuthenticationDefaults.Scheme,
                    _ => { });

            return services;
        }

        services
            .AddAuthentication(TrustedIdentityAuthenticationDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TrustedIdentityAuthenticationHandler>(
                TrustedIdentityAuthenticationDefaults.Scheme,
                _ => { });

        return services;
    }
}

public sealed class DevelopmentAuthenticationHandler(
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
        if (!Context.Request.Headers.TryGetValue("X-Dev-Role", out var roleValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var role = roleValues.ToString();
        if (!ValidRoles.Contains(role))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid X-Dev-Role."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Context.Request.Headers["X-Dev-Cpf"].FirstOrDefault() ?? "development-user"),
            new(ClaimTypes.Role, NormalizeRole(role))
        };

        AddOptionalClaim(claims, "cpf", "X-Dev-Cpf");
        if (!TryAddGuidClaim(claims, "clienteId", "X-Dev-ClienteId", out var failure))
        {
            return Task.FromResult(AuthenticateResult.Fail(failure));
        }

        if (!TryAddGuidClaim(claims, "funcionarioId", "X-Dev-FuncionarioId", out failure))
        {
            return Task.FromResult(AuthenticateResult.Fail(failure));
        }

        var identity = new ClaimsIdentity(claims, DevelopmentAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, DevelopmentAuthenticationDefaults.Scheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private void AddOptionalClaim(List<Claim> claims, string claimType, string headerName)
    {
        var value = Context.Request.Headers[headerName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new Claim(claimType, value));
        }
    }

    private bool TryAddGuidClaim(List<Claim> claims, string claimType, string headerName, out string failure)
    {
        failure = string.Empty;
        var value = Context.Request.Headers[headerName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Guid.TryParse(value, out var parsed))
        {
            failure = $"Invalid {headerName}.";
            return false;
        }

        claims.Add(new Claim(claimType, parsed.ToString("D")));
        return true;
    }

    private static string NormalizeRole(string role)
        => ValidRoles.First(validRole => string.Equals(validRole, role, StringComparison.OrdinalIgnoreCase));
}

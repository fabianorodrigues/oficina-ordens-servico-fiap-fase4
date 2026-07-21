using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Oficina.OrdensServico.Api.Middleware;
using Oficina.OrdensServico.Api.Observability;
using Oficina.OrdensServico.Api.Security;
using Oficina.OrdensServico.Api;
using Oficina.OrdensServico.Application;
using Oficina.OrdensServico.Application.Abstractions;
using Oficina.OrdensServico.Infrastructure;
using Oficina.OrdensServico.Infrastructure.Persistencia;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsProduction())
{
    builder.Configuration.AddKeyPerFile("/mnt/secrets-store", optional: true, reloadOnChange: false);
}

builder.Configuration.AddEnvironmentVariables();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

builder.Services.AddControllers();
builder.Services.AddOrdensApplication();
builder.Services.AddOrdensInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Oficina Ordens Servico API",
        Version = "v1"
    });
});

builder.Services.AddOficinaAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy(Policies.ClienteOnly, policy => policy.RequireRole(PerfisAcesso.Cliente));
    options.AddPolicy(Policies.FuncionarioOuAdmin, policy => policy.RequireRole(PerfisAcesso.Funcionario, PerfisAcesso.Admin));
    options.AddPolicy(Policies.AdminOnly, policy => policy.RequireRole(PerfisAcesso.Admin));
    options.AddPolicy(Policies.ClienteOuAdmin, policy => policy.RequireRole(PerfisAcesso.Cliente, PerfisAcesso.Admin));
});

builder.Services.AddOpenTelemetryFailOpen(
    builder.Configuration,
    builder.Logging,
    serviceName: "oficina-ordens-servico");

var app = builder.Build();

ProductionStartupValidation.Validate(app.Configuration, app.Environment);
await ApplyMigrationsIfEnabled(app);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "oficina-ordens-servico" }))
    .AllowAnonymous();
app.MapGet("/ready", () => Results.Ok(new { status = "Ready", service = "oficina-ordens-servico" }))
    .AllowAnonymous();

app.MapControllers();

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/ordens-servico/{id:guid}/forcar-compensacao",
        async (Guid id, HttpContext http, IFluxoDistribuidoOrdens fluxo, CancellationToken ct) =>
        {
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? http.TraceIdentifier;
            await fluxo.ForcarCompensacao(id, correlationId, ct);
            return Results.Accepted();
        }).RequireAuthorization();

    app.MapPost("/api/dev/ordens-servico/{id:guid}/reprocessar-reserva",
        async (Guid id, HttpContext http, IFluxoDistribuidoOrdens fluxo, CancellationToken ct) =>
        {
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? http.TraceIdentifier;
            await fluxo.ReprocessarReserva(id, correlationId, ct);
            return Results.Accepted();
        }).RequireAuthorization();
}

app.Run();

static async Task ApplyMigrationsIfEnabled(WebApplication app)
{
    var enabled = app.Configuration.GetValue("Database:ApplyMigrations", false);
    if (!enabled)
        return;

    if (!app.Environment.IsDevelopment())
        throw new InvalidOperationException("Database__ApplyMigrations=true so pode ser usado em Development.");

    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<OrdensServicoDbContext>().Database.MigrateAsync();
}

public partial class Program;

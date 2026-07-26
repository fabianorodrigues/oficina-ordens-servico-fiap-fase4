using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oficina.OrdensServico.Api.Middleware;
using Oficina.OrdensServico.Api.Observability;

namespace Oficina.OrdensServico.UnitTests;

public class OficinaJsonConsoleFormatterTests
{
    [Fact]
    public void Deve_emitir_os_campos_do_contrato_no_nivel_superior()
    {
        var json = Formatar(
            LogLevel.Information,
            "Ordem processada.",
            scope: new Dictionary<string, object> { ["CorrelationId"] = "abc-123" });

        Assert.Equal("INFO", json.GetProperty("level").GetString());
        Assert.Equal("Ordem processada.", json.GetProperty("message").GetString());
        Assert.Equal("oficina-ordens-servico", json.GetProperty("service.name").GetString());
        Assert.Equal("sha-1234567", json.GetProperty("service.version").GetString());
        Assert.Equal("production", json.GetProperty("deployment.environment").GetString());
        Assert.Equal("abc-123", json.GetProperty("correlationId").GetString());
        Assert.True(DateTimeOffset.TryParse(json.GetProperty("timestamp").GetString(), out _));
    }

    [Fact]
    public void Deve_omitir_trace_e_span_quando_nao_ha_activity()
    {
        // Emitir string vazia criaria no New Relic uma correlacao que nao existe.
        Assert.Null(Activity.Current);

        var json = Formatar(LogLevel.Information, "Sem activity.");

        Assert.False(json.TryGetProperty("trace.id", out _));
        Assert.False(json.TryGetProperty("span.id", out _));
    }

    [Fact]
    public void Deve_emitir_trace_e_span_quando_ha_activity()
    {
        using var source = new ActivitySource("Oficina.OrdensServico.Testes");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == "Oficina.OrdensServico.Testes",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("requisicao");
        Assert.NotNull(activity);

        var json = Formatar(LogLevel.Information, "Com activity.");

        Assert.Equal(activity!.TraceId.ToHexString(), json.GetProperty("trace.id").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), json.GetProperty("span.id").GetString());
    }

    [Fact]
    public void Deve_ignorar_chave_de_scope_fora_da_allowlist()
    {
        var json = Formatar(
            LogLevel.Information,
            "Mensagem.",
            scope: new Dictionary<string, object>
            {
                ["CorrelationId"] = "abc-123",
                ["Senha"] = "super-secreta",
                ["ConnectionString"] = "Server=tcp:x;Password=y"
            });

        Assert.Equal("abc-123", json.GetProperty("correlationId").GetString());
        Assert.False(json.TryGetProperty("Senha", out _));
        Assert.False(json.TryGetProperty("ConnectionString", out _));
        Assert.DoesNotContain("super-secreta", json.ToString());
    }

    [Fact]
    public void Deve_sanitizar_segredo_plantado_no_template_da_mensagem()
    {
        // A allowlist nao cobre message: um template ja existente pode colocar o
        // segredo diretamente no texto formatado.
        var json = Formatar(
            LogLevel.Error,
            "Falha conectando com Server=tcp:oficina.database.windows.net;User ID=app;Password=NaoDeveAparecer;");

        var message = json.GetProperty("message").GetString()!;
        Assert.DoesNotContain("NaoDeveAparecer", message);
        Assert.DoesNotContain("oficina.database.windows.net", message);
        Assert.Contains("***", message);
    }

    [Theory]
    [InlineData("Token recebido: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghij", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9")]
    [InlineData("Chave NRAK-ABCDEFGHIJKLMNOPQRST usada.", "NRAK-ABCDEFGHIJKLMNOPQRST")]
    [InlineData("Credencial AKIAIOSFODNN7EXAMPLE exposta.", "AKIAIOSFODNN7EXAMPLE")]
    public void Deve_sanitizar_tokens_e_chaves(string mensagem, string segredo)
    {
        var json = Formatar(LogLevel.Warning, mensagem);

        Assert.DoesNotContain(segredo, json.GetProperty("message").GetString());
    }

    [Fact]
    public void Deve_sanitizar_a_excecao_sem_serializar_exception_data()
    {
        var excecao = new InvalidOperationException("Falha em Password=NaoDeveAparecer;");
        excecao.Data["ConnectionString"] = "Server=tcp:x;Password=tambem-nao";

        var json = Formatar(LogLevel.Error, "Erro ao salvar.", excecao: excecao);

        Assert.Equal(typeof(InvalidOperationException).FullName, json.GetProperty("exception.type").GetString());
        Assert.DoesNotContain("NaoDeveAparecer", json.GetProperty("exception.message").GetString());
        Assert.DoesNotContain("tambem-nao", json.ToString());
    }

    [Fact]
    public void Deve_truncar_mensagem_muito_longa()
    {
        var json = Formatar(LogLevel.Information, new string('a', LogSanitizer.MaxMessageLength + 500));

        var message = json.GetProperty("message").GetString()!;
        Assert.EndsWith("...[truncated]", message);
        Assert.True(message.Length < LogSanitizer.MaxMessageLength + 500);
    }

    private static JsonElement Formatar(
        LogLevel level,
        string mensagem,
        IDictionary<string, object>? scope = null,
        Exception? excecao = null)
    {
        var formatter = new OficinaJsonConsoleFormatter(
            new OptionsMonitorStub(new OficinaJsonConsoleFormatterOptions
            {
                IncludeScopes = true,
                ServiceName = "oficina-ordens-servico",
                ServiceVersion = "sha-1234567",
                DeploymentEnvironment = "production"
            }));

        var scopeProvider = new LoggerExternalScopeProvider();
        IDisposable? escopo = scope is null ? null : scopeProvider.Push(scope);

        using var writer = new StringWriter();
        try
        {
            var entry = new LogEntry<string>(
                level,
                "Oficina.OrdensServico.Testes",
                new EventId(0),
                mensagem,
                excecao,
                static (state, _) => state);

            formatter.Write(in entry, scopeProvider, writer);
        }
        finally
        {
            escopo?.Dispose();
        }

        return JsonDocument.Parse(writer.ToString()).RootElement.Clone();
    }

    private sealed class OptionsMonitorStub(OficinaJsonConsoleFormatterOptions value)
        : IOptionsMonitor<OficinaJsonConsoleFormatterOptions>
    {
        public OficinaJsonConsoleFormatterOptions CurrentValue { get; } = value;
        public OficinaJsonConsoleFormatterOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<OficinaJsonConsoleFormatterOptions, string?> listener) => null;
    }
}

public class OficinaTelemetryResourceTests
{
    [Fact]
    public void Deve_ler_nome_versao_e_ambiente_das_variaveis_padronizadas()
    {
        var resource = OficinaTelemetryResource.Resolve(
            Configuracao(
                ("OTEL_SERVICE_NAME", "oficina-ordens-servico"),
                ("OTEL_SERVICE_VERSION", "abc1234"),
                ("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment=production,service.namespace=oficina")),
            "fallback");

        Assert.Equal("oficina-ordens-servico", resource.ServiceName);
        Assert.Equal("abc1234", resource.ServiceVersion);
        Assert.Equal("production", resource.DeploymentEnvironment);
    }

    [Fact]
    public void Deve_usar_o_nome_padrao_quando_a_variavel_esta_ausente()
    {
        var resource = OficinaTelemetryResource.Resolve(Configuracao(), "oficina-ordens-servico");

        Assert.Equal("oficina-ordens-servico", resource.ServiceName);
        Assert.Null(resource.ServiceVersion);
        Assert.Null(resource.DeploymentEnvironment);
    }

    private static IConfiguration Configuracao(params (string Chave, string Valor)[] valores)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(valores.Select(x => new KeyValuePair<string, string?>(x.Chave, x.Valor)))
            .Build();
}

public class CorrelationIdMiddlewareObservabilidadeTests
{
    [Fact]
    public async Task Deve_registrar_um_log_final_no_caminho_de_sucesso()
    {
        var logger = new ListLogger();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/clientes";

        await middleware.Invoke(context);

        var registro = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, registro.Level);
        Assert.Contains("completed with", registro.Message);
    }

    [Fact]
    public async Task Deve_registrar_um_log_final_quando_o_pipeline_lanca_excecao()
    {
        // O log vai em finally: requisicao que falha e justamente a que mais
        // precisa aparecer correlacionada.
        var logger = new ListLogger();
        var middleware = new CorrelationIdMiddleware(
            _ => throw new InvalidOperationException("falha no endpoint"),
            logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/clientes";

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.Invoke(context));

        var registro = Assert.Single(logger.Entries);
        Assert.Contains("Failed: True", registro.Message);
    }

    [Fact]
    public async Task Deve_registrar_a_sonda_de_prontidao_em_debug()
    {
        var logger = new ListLogger();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/ready";

        await middleware.Invoke(context);

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task Deve_marcar_o_correlation_id_no_span_corrente()
    {
        using var source = new ActivitySource("Oficina.OrdensServico.Testes.Middleware");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == "Oficina.OrdensServico.Testes.Middleware",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("requisicao");
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "correlacao-de-span";
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask, NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.Invoke(context);

        Assert.Equal("correlacao-de-span", activity!.GetTagItem("correlationId"));
    }

    private sealed class ListLogger : ILogger<CorrelationIdMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoopScope();
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}

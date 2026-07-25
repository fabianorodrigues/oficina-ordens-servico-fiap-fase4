using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oficina.OrdensServico.Api;
using Oficina.OrdensServico.Api.Observability;
using Oficina.OrdensServico.Api.Security;
using Oficina.OrdensServico.Infrastructure.Http;
using Oficina.OrdensServico.Infrastructure.Messaging;

namespace Oficina.OrdensServico.UnitTests;

public class ProductionStartupValidationTests
{
    private static Dictionary<string, string?> ConfiguracaoValida() => new()
    {
        ["ConnectionStrings:DefaultConnection"] = "Server=servidor.example.invalid;Database=OficinaOrdensServicoDb;User Id=ordens_app;TrustServerCertificate=True",
        ["Messaging:Sqs:Region"] = "us-east-1",
        ["Messaging:Sqs:CommandsQueueUrl"] = "https://sqs.example.invalid/comandos",
        ["Messaging:Sqs:CommandsDlqQueueUrl"] = "https://sqs.example.invalid/comandos-dlq",
        ["Messaging:Sqs:EventsQueueUrl"] = "https://sqs.example.invalid/eventos",
        ["Messaging:Sqs:EventsDlqQueueUrl"] = "https://sqs.example.invalid/eventos-dlq",
        ["Integrations:Cadastro:BaseUrl"] = "http://alb.example.invalid",
        ["Integrations:Estoque:BaseUrl"] = "http://alb.example.invalid",
        ["Payments:UseMock"] = "true",
        ["Payments:MockBehavior"] = "Approved",
        ["Payments:ExternalApiEnabled"] = "false",
        ["Payments:ExternalWebhookEnabled"] = "false",
        ["Payments:ContractStatus"] = "Pending",
        ["Messaging:Sqs:ConsumerConcurrency"] = "1",
        ["Messaging:Sqs:MaxMessagesPerReceive"] = "1",
        ["Messaging:Sqs:WaitTimeSeconds"] = "20",
        ["Messaging:Sqs:VisibilityTimeoutSeconds"] = "60"
    };

    private static void Validar(Dictionary<string, string?> valores, string ambiente = "Production")
        => ProductionStartupValidation.Validate(
            new ConfigurationBuilder().AddInMemoryCollection(valores).Build(),
            new HostEnvironmentStub(ambiente));

    [Fact]
    public void Configuracao_completa_deve_passar()
    {
        Assert.Null(Record.Exception(() => Validar(ConfiguracaoValida())));
    }

    [Fact]
    public void Fora_de_producao_a_validacao_nao_se_aplica()
    {
        // Em Development o ambiente local usa LocalStack e valores parciais: a
        // validacao rigida so faz sentido no ambiente publicado.
        Assert.Null(Record.Exception(() => Validar([], "Development")));
    }

    [Theory]
    [InlineData("ConnectionStrings:DefaultConnection", "A connection string obrigatoria nao foi configurada.")]
    [InlineData("Messaging:Sqs:Region", "A regiao AWS obrigatoria nao foi configurada.")]
    [InlineData("Messaging:Sqs:CommandsQueueUrl", "A URL da fila de comandos nao foi configurada.")]
    [InlineData("Messaging:Sqs:CommandsDlqQueueUrl", "A URL da DLQ de comandos nao foi configurada.")]
    [InlineData("Messaging:Sqs:EventsQueueUrl", "A URL da fila de eventos nao foi configurada.")]
    [InlineData("Messaging:Sqs:EventsDlqQueueUrl", "A URL da DLQ de eventos nao foi configurada.")]
    [InlineData("Integrations:Cadastro:BaseUrl", "A URL interna do Cadastro nao foi configurada.")]
    [InlineData("Integrations:Estoque:BaseUrl", "A URL interna do Estoque nao foi configurada.")]
    public void Deve_exigir_cada_valor_obrigatorio(string chave, string mensagem)
    {
        var valores = ConfiguracaoValida();
        valores.Remove(chave);

        var erro = Assert.Throws<InvalidOperationException>(() => Validar(valores));

        Assert.Equal(mensagem, erro.Message);
    }

    [Fact]
    public void Connection_string_alternativa_tambem_e_aceita()
    {
        var valores = ConfiguracaoValida();
        valores.Remove("ConnectionStrings:DefaultConnection");
        valores["ConnectionStrings:OficinaOrdensServicoDb"] = "Server=servidor.example.invalid;Database=OficinaOrdensServicoDb;TrustServerCertificate=True";

        Assert.Null(Record.Exception(() => Validar(valores)));
    }

    [Fact]
    public void Migrations_automaticas_sao_proibidas_em_producao()
    {
        var valores = ConfiguracaoValida();
        valores["Database:ApplyMigrations"] = "true";

        var erro = Assert.Throws<InvalidOperationException>(() => Validar(valores));

        Assert.Equal("Database__ApplyMigrations=true so pode ser usado em Development.", erro.Message);
    }

    [Fact]
    public void Autenticacao_de_desenvolvimento_e_proibida_em_producao()
    {
        var valores = ConfiguracaoValida();
        valores["Authentication:Mode"] = "Development";

        var erro = Assert.Throws<InvalidOperationException>(() => Validar(valores));

        Assert.Equal("Authentication Development nao pode ser usado em Production.", erro.Message);
    }

    [Fact]
    public void Pagamento_precisa_estar_no_modo_mock()
    {
        var valores = ConfiguracaoValida();
        valores["Payments:UseMock"] = "false";
        valores["Payments:Mode"] = "Api";

        var erro = Assert.Throws<InvalidOperationException>(() => Validar(valores));

        Assert.Equal("Payments__UseMock=true deve estar habilitado nesta versao.", erro.Message);
    }

    [Fact]
    public void Modo_mock_explicito_dispensa_a_flag_use_mock()
    {
        var valores = ConfiguracaoValida();
        valores["Payments:UseMock"] = "false";
        valores["Payments:Mode"] = "Mock";

        Assert.Null(Record.Exception(() => Validar(valores)));
    }

    [Fact]
    public void Comportamento_do_mock_precisa_ser_aprovado()
    {
        var valores = ConfiguracaoValida();
        valores["Payments:MockBehavior"] = "Rejected";

        var erro = Assert.Throws<InvalidOperationException>(() => Validar(valores));

        Assert.Equal("Payments__MockBehavior=Approved deve ser usado nesta versao.", erro.Message);
    }

    [Theory]
    [InlineData("Payments:ExternalApiEnabled", "true", "Payments__ExternalApiEnabled deve permanecer false enquanto o contrato esta pendente.")]
    [InlineData("Payments:ExternalWebhookEnabled", "true", "Payments__ExternalWebhookEnabled deve permanecer false enquanto o contrato esta pendente.")]
    [InlineData("Payments:ContractStatus", "Signed", "Payments__ContractStatus=Pending deve ser usado nesta versao.")]
    public void Integracao_externa_deve_permanecer_desligada(string chave, string valor, string mensagem)
    {
        var valores = ConfiguracaoValida();
        valores[chave] = valor;

        var erro = Assert.Throws<InvalidOperationException>(() => Validar(valores));

        Assert.Equal(mensagem, erro.Message);
    }

    [Theory]
    [InlineData("Messaging:Sqs:ConsumerConcurrency", "2", "Consumer concurrency deve ser igual a 1.")]
    [InlineData("Messaging:Sqs:MaxMessagesPerReceive", "10", "Max messages deve ser igual a 1.")]
    [InlineData("Messaging:Sqs:WaitTimeSeconds", "0", "WaitTimeSeconds invalido.")]
    [InlineData("Messaging:Sqs:VisibilityTimeoutSeconds", "0", "VisibilityTimeoutSeconds invalido.")]
    public void Parametros_de_consumo_devem_respeitar_o_contrato(string chave, string valor, string mensagem)
    {
        var valores = ConfiguracaoValida();
        valores[chave] = valor;

        var erro = Assert.Throws<InvalidOperationException>(() => Validar(valores));

        Assert.Equal(mensagem, erro.Message);
    }

    private sealed class HostEnvironmentStub(string ambiente) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = ambiente;
        public string ApplicationName { get; set; } = "Oficina.OrdensServico.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

public class OrdensMessageJsonTests
{
    private static string EnvelopeValido(Guid ordemServicoId)
        => MessageJson.Envelope(
            OrdensMessageTypes.ReservarEstoque, ordemServicoId, "correlacao-1", "causa-1",
            new ReservarEstoquePayload("chave-1", [new ReservarEstoqueItemPayload(1, Guid.NewGuid(), 2)]));

    [Fact]
    public void Envelope_deve_respeitar_o_contrato()
    {
        var ordemServicoId = Guid.NewGuid();

        var envelope = MessageJson.ParseAndValidate(EnvelopeValido(ordemServicoId));

        Assert.NotEqual(Guid.Empty, envelope.MessageId);
        Assert.Equal(OrdensMessageTypes.ReservarEstoque, envelope.MessageType);
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Equal(ordemServicoId, envelope.OrdemServicoId);
        Assert.Equal("causa-1", envelope.CausationId);
    }

    [Fact]
    public void Payload_deve_sobreviver_ao_round_trip()
    {
        var materialId = Guid.NewGuid();
        var body = MessageJson.Envelope(
            OrdensMessageTypes.ReservarEstoque, Guid.NewGuid(), "c1", null,
            new ReservarEstoquePayload("chave-1", [new ReservarEstoqueItemPayload(1, materialId, 4)]));

        var payload = MessageJson.ParseAndValidate(body).Payload.Deserialize<ReservarEstoquePayload>(MessageJson.Options)!;

        Assert.Equal(materialId, payload.Itens[0].MaterialId);
        Assert.Equal(4, payload.Itens[0].Quantidade);
    }

    [Fact]
    public void Deve_recusar_envelope_nulo()
    {
        var erro = Assert.Throws<InvalidOperationException>(() => MessageJson.ParseAndValidate("null"));
        Assert.Equal("Envelope ausente.", erro.Message);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "EstoqueReservado", 1, "22222222-2222-2222-2222-222222222222", "c1", "MessageId invalido.")]
    [InlineData("11111111-1111-1111-1111-111111111111", "", 1, "22222222-2222-2222-2222-222222222222", "c1", "MessageType invalido.")]
    [InlineData("11111111-1111-1111-1111-111111111111", "EstoqueReservado", 3, "22222222-2222-2222-2222-222222222222", "c1", "SchemaVersion invalida.")]
    [InlineData("11111111-1111-1111-1111-111111111111", "EstoqueReservado", 1, "00000000-0000-0000-0000-000000000000", "c1", "OrdemServicoId invalido.")]
    [InlineData("11111111-1111-1111-1111-111111111111", "EstoqueReservado", 1, "22222222-2222-2222-2222-222222222222", " ", "CorrelationId invalido.")]
    public void Deve_recusar_envelope_fora_do_contrato(
        string messageId, string messageType, int schemaVersion, string ordemServicoId, string correlationId, string mensagem)
    {
        var body = JsonSerializer.Serialize(new
        {
            messageId,
            messageType,
            schemaVersion,
            occurredAtUtc = DateTimeOffset.UtcNow,
            correlationId,
            causationId = (string?)null,
            ordemServicoId,
            payload = new { }
        });

        var erro = Assert.Throws<InvalidOperationException>(() => MessageJson.ParseAndValidate(body));
        Assert.Equal(mensagem, erro.Message);
    }
}

public class OrdensInboxOutboxTests
{
    private static InboxMessage NovoInbox() => new(
        Guid.NewGuid(), OrdensMessageTypes.EstoqueReservado, Guid.NewGuid(), "correlacao-1", "{}");

    private static OutboxMessage NovoOutbox() => new(
        Guid.NewGuid(), OrdensMessageTypes.ReservarEstoque, Guid.NewGuid(), "correlacao-1", null, "{}");

    [Fact]
    public void Inbox_deve_nascer_recebida()
    {
        var inbox = NovoInbox();

        Assert.Equal(InboxMessageStatus.Received, inbox.Status);
        Assert.Equal(0, inbox.Attempts);
        Assert.Null(inbox.ProcessedAtUtc);
    }

    [Fact]
    public void Inbox_deve_contar_tentativas_ao_reivindicar()
    {
        var inbox = NovoInbox();
        var ate = DateTimeOffset.UtcNow.AddMinutes(1);

        inbox.Claim(ate);
        inbox.Claim(ate);

        Assert.Equal(InboxMessageStatus.Processing, inbox.Status);
        Assert.Equal(2, inbox.Attempts);
    }

    [Fact]
    public void Inbox_processada_deve_limpar_lock_e_erro()
    {
        var inbox = NovoInbox();
        inbox.MarkDeferred("fora de ordem");

        inbox.MarkProcessed();

        Assert.Equal(InboxMessageStatus.Processed, inbox.Status);
        Assert.Null(inbox.Error);
        Assert.Null(inbox.LockedUntilUtc);
        Assert.NotNull(inbox.ProcessedAtUtc);
    }

    [Fact]
    public void Inbox_diferida_deve_voltar_com_atraso()
    {
        var inbox = NovoInbox();

        inbox.MarkDeferred("reserva ainda nao existe");

        Assert.Equal(InboxMessageStatus.Deferred, inbox.Status);
        Assert.True(inbox.LockedUntilUtc > DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(true, InboxMessageStatus.DeadLettered)]
    [InlineData(false, InboxMessageStatus.Received)]
    public void Inbox_falha_deve_escolher_entre_dlq_e_retentativa(bool deadLetter, InboxMessageStatus esperado)
    {
        var inbox = NovoInbox();

        inbox.MarkFailed(new string('x', 900), deadLetter);

        Assert.Equal(esperado, inbox.Status);
        Assert.Equal(500, inbox.Error!.Length);
    }

    [Fact]
    public void Outbox_deve_publicar_e_limpar_o_erro_anterior()
    {
        var outbox = NovoOutbox();
        outbox.Claim(DateTimeOffset.UtcNow.AddMinutes(1));
        outbox.MarkFailed("timeout");

        outbox.MarkPublished();

        Assert.Equal(1, outbox.Attempts);
        Assert.NotNull(outbox.PublishedAtUtc);
        Assert.Null(outbox.Error);
        Assert.Null(outbox.LockedUntilUtc);
    }

    [Fact]
    public void Outbox_falha_deve_liberar_para_nova_tentativa()
    {
        var outbox = NovoOutbox();
        outbox.Claim(DateTimeOffset.UtcNow.AddMinutes(1));

        outbox.MarkFailed(new string('y', 900));

        Assert.Null(outbox.LockedUntilUtc);
        Assert.Null(outbox.PublishedAtUtc);
        Assert.Equal(500, outbox.Error!.Length);
    }
}

public class OrdensSqsMessagingRegistrationTests
{
    private static IConfiguration Configuracao(Dictionary<string, string?> valores)
        => new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

    [Fact]
    public void Mensageria_desabilitada_nao_registra_consumidores()
    {
        var services = new ServiceCollection();

        services.AddOrdensMessaging(Configuracao(new Dictionary<string, string?>
        {
            ["Messaging:Sqs:Enabled"] = "false"
        }));

        Assert.DoesNotContain(services, x => x.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Mensageria_habilitada_registra_receiver_inbox_e_outbox()
    {
        var services = new ServiceCollection();

        services.AddOrdensMessaging(Configuracao(new Dictionary<string, string?>
        {
            ["Messaging:Sqs:Enabled"] = "true",
            ["Messaging:Sqs:Region"] = "us-east-1"
        }));

        Assert.Equal(3, services.Count(x => x.ServiceType == typeof(IHostedService)));
    }

    [Fact]
    public void Endpoint_local_usa_credenciais_explicitas()
    {
        var services = new ServiceCollection();

        services.AddOrdensMessaging(Configuracao(new Dictionary<string, string?>
        {
            ["Messaging:Sqs:Enabled"] = "true",
            ["Messaging:Sqs:Region"] = "us-east-1",
            ["Messaging:Sqs:ServiceUrl"] = "http://localstack:4566"
        }));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<Amazon.SQS.IAmazonSQS>());
    }

    [Fact]
    public void Sem_endpoint_local_usa_a_cadeia_padrao_de_credenciais()
    {
        // Sem ServiceUrl o cliente resolve credenciais pela cadeia padrao, que
        // no cluster chega ao IMDS e a role da instancia.
        var services = new ServiceCollection();

        services.AddOrdensMessaging(Configuracao(new Dictionary<string, string?>
        {
            ["Messaging:Sqs:Enabled"] = "true",
            ["Messaging:Sqs:Region"] = "us-east-1"
        }));

        Assert.Contains(services, x => x.ServiceType == typeof(Amazon.SQS.IAmazonSQS));
    }
}

public class CorrelationHeaderHandlerTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Ultima { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Ultima = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<HttpRequestMessage> Enviar(HttpContext? contexto)
    {
        var capturing = new CapturingHandler();
        var accessor = new HttpContextAccessor { HttpContext = contexto };
        var handler = new CorrelationHeaderHandler(accessor) { InnerHandler = capturing };
        using var client = new HttpClient(handler);

        await client.GetAsync("http://servico.example.invalid/api/recurso");
        return capturing.Ultima!;
    }

    [Fact]
    public async Task Sem_contexto_http_nao_deve_propagar_cabecalho()
    {
        var enviado = await Enviar(null);

        Assert.False(enviado.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public async Task Deve_propagar_o_correlation_id_da_requisicao_de_entrada()
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Headers["X-Correlation-Id"] = "correlacao-1";

        var enviado = await Enviar(contexto);

        Assert.Equal("correlacao-1", enviado.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Deve_propagar_a_identidade_validada_na_borda()
    {
        // Sem repassar a identidade, o servico chamado recebe a requisicao sem
        // principal e responde 401.
        var contexto = new DefaultHttpContext();
        contexto.Request.Headers["x-oficina-user-id"] = "11111111-1111-1111-1111-111111111111";
        contexto.Request.Headers["x-oficina-user-role"] = "Funcionario";
        contexto.Request.Headers["x-oficina-user-cpf"] = "12345678901";
        contexto.Request.Headers["x-oficina-user-name"] = "Maria";

        var enviado = await Enviar(contexto);

        Assert.Equal("11111111-1111-1111-1111-111111111111", enviado.Headers.GetValues("x-oficina-user-id").Single());
        Assert.Equal("Funcionario", enviado.Headers.GetValues("x-oficina-user-role").Single());
        Assert.Equal("12345678901", enviado.Headers.GetValues("x-oficina-user-cpf").Single());
        Assert.Equal("Maria", enviado.Headers.GetValues("x-oficina-user-name").Single());
    }

    [Fact]
    public async Task Deve_propagar_os_cabecalhos_de_desenvolvimento()
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Headers["X-Dev-Role"] = "Funcionario";
        contexto.Request.Headers["X-Dev-Cpf"] = "12345678901";

        var enviado = await Enviar(contexto);

        Assert.Equal("Funcionario", enviado.Headers.GetValues("X-Dev-Role").Single());
        Assert.Equal("12345678901", enviado.Headers.GetValues("X-Dev-Cpf").Single());
    }

    [Fact]
    public async Task Cabecalhos_ausentes_nao_devem_ser_inventados()
    {
        var enviado = await Enviar(new DefaultHttpContext());

        Assert.False(enviado.Headers.Contains("x-oficina-user-id"));
        Assert.False(enviado.Headers.Contains("X-Dev-Role"));
    }
}

public class OrdensIdentidadeTests
{
    private sealed class OptionsMonitorStub : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private static async Task<AuthenticateResult> Development(Action<HttpContext> configurar)
    {
        var handler = new DevelopmentAuthenticationHandler(
            new OptionsMonitorStub(), NullLoggerFactory.Instance, UrlEncoder.Default);
        var scheme = new AuthenticationScheme(
            DevelopmentAuthenticationDefaults.Scheme, DevelopmentAuthenticationDefaults.Scheme,
            typeof(DevelopmentAuthenticationHandler));
        var context = new DefaultHttpContext();
        configurar(context);
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private static async Task<AuthenticateResult> Trusted(Action<HttpContext> configurar)
    {
        var handler = new TrustedIdentityAuthenticationHandler(
            new OptionsMonitorStub(), NullLoggerFactory.Instance, UrlEncoder.Default);
        var scheme = new AuthenticationScheme(
            TrustedIdentityAuthenticationDefaults.Scheme, TrustedIdentityAuthenticationDefaults.Scheme,
            typeof(TrustedIdentityAuthenticationHandler));
        var context = new DefaultHttpContext();
        configurar(context);
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task Development_sem_papel_nao_produz_resultado()
    {
        var resultado = await Development(_ => { });

        Assert.False(resultado.Succeeded);
        Assert.Null(resultado.Failure);
    }

    [Fact]
    public async Task Development_com_papel_invalido_falha()
    {
        var resultado = await Development(ctx => ctx.Request.Headers["X-Dev-Role"] = "Sindico");

        Assert.Equal("Invalid X-Dev-Role.", resultado.Failure?.Message);
    }

    [Theory]
    [InlineData("cliente", "Cliente")]
    [InlineData("FUNCIONARIO", "Funcionario")]
    [InlineData("Admin", "Admin")]
    public async Task Development_normaliza_o_papel(string entrada, string esperado)
    {
        var resultado = await Development(ctx => ctx.Request.Headers["X-Dev-Role"] = entrada);

        Assert.Equal(esperado, resultado.Principal!.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task Development_projeta_identificadores_informados()
    {
        var clienteId = Guid.NewGuid();

        var resultado = await Development(ctx =>
        {
            ctx.Request.Headers["X-Dev-Role"] = "Cliente";
            ctx.Request.Headers["X-Dev-Cpf"] = "12345678901";
            ctx.Request.Headers["X-Dev-ClienteId"] = clienteId.ToString();
        });

        Assert.Equal("12345678901", resultado.Principal!.FindFirstValue("cpf"));
        Assert.Equal(clienteId.ToString("D"), resultado.Principal.FindFirstValue("clienteId"));
    }

    [Fact]
    public async Task Development_sem_cpf_usa_identificador_padrao()
    {
        var resultado = await Development(ctx => ctx.Request.Headers["X-Dev-Role"] = "Admin");

        Assert.Equal("development-user", resultado.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Theory]
    [InlineData("X-Dev-ClienteId")]
    [InlineData("X-Dev-FuncionarioId")]
    public async Task Development_com_identificador_invalido_falha(string cabecalho)
    {
        var resultado = await Development(ctx =>
        {
            ctx.Request.Headers["X-Dev-Role"] = "Funcionario";
            ctx.Request.Headers[cabecalho] = "nao-e-guid";
        });

        Assert.Equal($"Invalid {cabecalho}.", resultado.Failure?.Message);
    }

    [Fact]
    public async Task Trusted_sem_cabecalhos_nao_produz_resultado()
    {
        var resultado = await Trusted(_ => { });

        Assert.False(resultado.Succeeded);
        Assert.Null(resultado.Failure);
    }

    [Fact]
    public async Task Trusted_sem_identificador_falha()
    {
        var resultado = await Trusted(ctx =>
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserRoleHeader] = "Funcionario");

        Assert.Equal("Identidade sem identificador.", resultado.Failure?.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sindico")]
    public async Task Trusted_com_perfil_invalido_falha(string papel)
    {
        var resultado = await Trusted(ctx =>
        {
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserIdHeader] = Guid.NewGuid().ToString();
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserRoleHeader] = papel;
        });

        Assert.Equal("Identidade com perfil invalido.", resultado.Failure?.Message);
    }

    [Fact]
    public async Task Trusted_cliente_recebe_claim_de_cliente()
    {
        var id = Guid.NewGuid();

        var resultado = await Trusted(ctx =>
        {
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserIdHeader] = id.ToString();
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserRoleHeader] = "cliente";
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserCpfHeader] = "12345678901";
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserNameHeader] = "Maria";
        });

        Assert.Equal("Cliente", resultado.Principal!.FindFirstValue(ClaimTypes.Role));
        Assert.Equal(id.ToString("D"), resultado.Principal.FindFirstValue("clienteId"));
        Assert.Equal("Maria", resultado.Principal.FindFirstValue(ClaimTypes.Name));
    }

    [Fact]
    public async Task Trusted_funcionario_recebe_claim_de_funcionario()
    {
        var id = Guid.NewGuid();

        var resultado = await Trusted(ctx =>
        {
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserIdHeader] = id.ToString();
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserRoleHeader] = "Admin";
        });

        Assert.Equal(id.ToString("D"), resultado.Principal!.FindFirstValue("funcionarioId"));
    }

    [Fact]
    public async Task Trusted_com_identificador_nao_guid_nao_deriva_claim()
    {
        var resultado = await Trusted(ctx =>
        {
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserIdHeader] = "usuario-externo";
            ctx.Request.Headers[TrustedIdentityAuthenticationDefaults.UserRoleHeader] = "Funcionario";
        });

        Assert.True(resultado.Succeeded);
        Assert.Null(resultado.Principal!.FindFirstValue("funcionarioId"));
    }
}

public class OrdensObservabilidadeTests
{
    private sealed class LoggingBuilderStub(IServiceCollection services) : ILoggingBuilder
    {
        public IServiceCollection Services { get; } = services;
    }

    private sealed class ConfiguracaoQueFalha : IConfiguration
    {
        public string? this[string key]
        {
            get => throw new InvalidOperationException("Provedor indisponivel.");
            set => throw new InvalidOperationException("Provedor indisponivel.");
        }

        public IEnumerable<IConfigurationSection> GetChildren() => throw new InvalidOperationException();
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => throw new InvalidOperationException();
        public IConfigurationSection GetSection(string key) => throw new InvalidOperationException();
    }

    [Fact]
    public void Telemetria_desabilitada_nao_registra_nada()
    {
        var services = new ServiceCollection();

        services.AddOpenTelemetryFailOpen(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:Enabled"] = "false"
            }).Build(),
            new LoggingBuilderStub(services), "oficina-ordens-servico");

        Assert.DoesNotContain(services, x => x.ServiceType.FullName?.Contains("OpenTelemetry") == true);
    }

    [Fact]
    public void Telemetria_habilitada_com_endpoint_registra_exporter()
    {
        var services = new ServiceCollection();

        services.AddOpenTelemetryFailOpen(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:OtlpEndpoint"] = "http://collector.example.invalid:4317"
            }).Build(),
            new LoggingBuilderStub(services), "oficina-ordens-servico");

        Assert.Contains(services, x => x.ServiceType.FullName?.Contains("OpenTelemetry") == true);
    }

    [Fact]
    public void Falha_na_telemetria_nao_derruba_a_aplicacao()
    {
        var services = new ServiceCollection();

        var excecao = Record.Exception(() => services.AddOpenTelemetryFailOpen(
            new ConfiguracaoQueFalha(), new LoggingBuilderStub(services), "oficina-ordens-servico"));

        Assert.Null(excecao);
        Assert.Contains(services, x => x.ImplementationFactory is not null);
    }
}

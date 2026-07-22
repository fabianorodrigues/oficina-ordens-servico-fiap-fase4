using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oficina.OrdensServico.Infrastructure;
using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.IntegrationTests;

public sealed class PaymentIntegrationTests
{
    [Fact]
    public void UseMock_true_contract_pending_inicia_sem_base_url()
    {
        var services = new ServiceCollection();
        services.AddOrdensInfrastructure(Config(
            ("Payments:UseMock", "true"),
            ("Payments:MockBehavior", "Approved"),
            ("Payments:ExternalApiEnabled", "false"),
            ("Payments:ExternalWebhookEnabled", "false"),
            ("Payments:ContractStatus", "Pending"),
            ("Messaging:Sqs:Enabled", "false")));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<MockPagamentoGateway>(provider.GetRequiredService<IPagamentoGateway>());
    }

    [Fact]
    public void UseMock_false_contract_pending_falha()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddOrdensInfrastructure(Config(
            ("Payments:UseMock", "false"),
            ("Payments:ExternalApiEnabled", "true"),
            ("Payments:ExternalWebhookEnabled", "true"),
            ("Payments:ContractStatus", "Pending"),
            ("Payments:BaseUrl", "https://payments.example.invalid"),
            ("Payments:SubmitPath", "/pending"),
            ("Application:PublicBaseUrl", "https://ordens.example.invalid"),
            ("Messaging:Sqs:Enabled", "false"))));

        Assert.Contains("ContractStatus=Ready", ex.Message);
    }

    [Fact]
    public async Task Mapper_pendente_lanca_erro_controlado()
    {
        var mapper = new PendingExternalPaymentContractMapper();
        await Assert.ThrowsAsync<PaymentContractPendingException>(() =>
            mapper.ReadSubmissionResponseAsync(new HttpResponseMessage(System.Net.HttpStatusCode.Accepted), CancellationToken.None));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();
}

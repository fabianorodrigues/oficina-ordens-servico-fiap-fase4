using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.OrdensServico.Api.Controllers;
using Oficina.OrdensServico.Api.Security;
using Oficina.OrdensServico.Application.Contracts;

namespace Oficina.OrdensServico.ContractTests;

public sealed class PublicContractsTests
{
    [Theory]
    [InlineData(typeof(OrdensServicoController), "api/ordens-servico", Policies.FuncionarioOuAdmin)]
    [InlineData(typeof(OrcamentosController), "api/orcamentos", Policies.FuncionarioOuAdmin)]
    [InlineData(typeof(MinhasOrdensServicoController), "api/minhas-ordens-servico", Policies.ClienteOnly)]
    [InlineData(typeof(MeusOrcamentosController), "api/meus-orcamentos", Policies.ClienteOnly)]
    [InlineData(typeof(RelatoriosController), "api/relatorios", Policies.FuncionarioOuAdmin)]
    public void Rotas_e_policies_publicas_preservadas(Type type, string route, string policy)
    {
        Assert.Equal(route, type.GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single().Template);
        Assert.Equal(policy, type.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().Single().Policy);
    }

    [Fact]
    public void Webhook_pagamentos_e_anonimo_e_fica_em_rota_dedicada()
    {
        Assert.Equal("api/webhooks/payments", typeof(PaymentWebhooksController)
            .GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>()
            .Single()
            .Template);
        Assert.NotEmpty(typeof(PaymentWebhooksController).GetCustomAttributes(typeof(AllowAnonymousAttribute), false));
        Assert.Empty(typeof(PaymentWebhooksController).GetCustomAttributes(typeof(AuthorizeAttribute), false));
    }

    [Fact]
    public void Request_de_abertura_preserva_shape_estavel()
    {
        var req = new AbrirOrdemServicoRequest();
        Assert.NotNull(req.Cliente);
        Assert.NotNull(req.Veiculo);
        Assert.NotNull(req.Itens);
    }
}

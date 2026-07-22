using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.OrdensServico.Api.Security;
using Oficina.OrdensServico.Application.Shared;
using Oficina.OrdensServico.Application.UseCases;

namespace Oficina.OrdensServico.Api.Controllers;

[ApiController]
[Route("api/minhas-ordens-servico")]
[Authorize(Policy = Policies.ClienteOnly)]
public class MinhasOrdensServicoController(OrdensUseCases useCases) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) => Ok(await useCases.ListarMinhas(ClienteId(), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct) => Ok(await useCases.ObterDoCliente(id, ClienteId(), ct));

    [HttpGet("{id:guid}/status")]
    public async Task<IActionResult> ObterStatus(Guid id, CancellationToken ct) => Ok(await useCases.StatusDoCliente(id, ClienteId(), ct));

    private Guid ClienteId()
    {
        var value = User.FindFirst("clienteId")?.Value;
        return Guid.TryParse(value, out var id) ? id : throw new OrdensException("Cliente autenticado invalido.", 401);
    }
}

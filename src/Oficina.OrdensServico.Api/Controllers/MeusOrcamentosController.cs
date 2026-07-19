using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.OrdensServico.Api.Security;
using Oficina.OrdensServico.Application.Shared;
using Oficina.OrdensServico.Application.UseCases;

namespace Oficina.OrdensServico.Api.Controllers;

[ApiController]
[Route("api/meus-orcamentos")]
[Authorize(Policy = Policies.ClienteOnly)]
public class MeusOrcamentosController(OrdensUseCases useCases) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Obter(Guid id, CancellationToken ct) => Ok(await useCases.ObterOrcamentoDoCliente(id, ClienteId(), ct));

    [HttpPost("{id:guid}/aprovar")]
    public async Task<IActionResult> Aprovar(Guid id, CancellationToken ct) { await useCases.AprovarOrcamentoDoCliente(id, ClienteId(), ct); return NoContent(); }

    [HttpPost("{id:guid}/recusar")]
    public async Task<IActionResult> Recusar(Guid id, CancellationToken ct) { await useCases.RecusarOrcamentoDoCliente(id, ClienteId(), ct); return NoContent(); }

    private Guid ClienteId()
    {
        var value = User.FindFirst("clienteId")?.Value;
        return Guid.TryParse(value, out var id) ? id : throw new OrdensException("Cliente autenticado invalido.", 401);
    }
}

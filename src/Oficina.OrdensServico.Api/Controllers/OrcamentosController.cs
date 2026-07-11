using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.OrdensServico.Api.Security;
using Oficina.OrdensServico.Application.UseCases;

namespace Oficina.OrdensServico.Api.Controllers;

[ApiController]
[Route("api/orcamentos")]
[Authorize(Policy = Policies.FuncionarioOuAdmin)]
public class OrcamentosController(OrdensUseCases useCases) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> ObterPorId(Guid id, CancellationToken ct) => Ok(await useCases.ObterOrcamento(id, ct));

    [HttpPost("{id:guid}/aprovar")]
    public async Task<IActionResult> Aprovar(Guid id, CancellationToken ct) { await useCases.AprovarOrcamento(id, ct); return NoContent(); }

    [HttpPost("{id:guid}/recusar")]
    public async Task<IActionResult> Recusar(Guid id, CancellationToken ct) { await useCases.RecusarOrcamento(id, ct); return NoContent(); }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.OrdensServico.Api.Security;
using Oficina.OrdensServico.Application.Contracts;
using Oficina.OrdensServico.Application.UseCases;

namespace Oficina.OrdensServico.Api.Controllers;

[ApiController]
[Route("api/ordens-servico")]
[Authorize(Policy = Policies.FuncionarioOuAdmin)]
public class OrdensServicoController(OrdensUseCases useCases) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Abrir([FromBody] AbrirOrdemServicoRequest req, CancellationToken ct)
    {
        var result = await useCases.Abrir(req, ct);
        return CreatedAtAction(nameof(ObterPorId), new { id = result.Id }, result);
    }

    [HttpPost("{id:guid}/classificar")]
    public async Task<IActionResult> Classificar(Guid id, [FromBody] ClassificarOrdemServicoRequest req, CancellationToken ct) { await useCases.Classificar(id, req.TipoManutencao, ct); return NoContent(); }

    [HttpPost("{id:guid}/diagnostico")]
    public async Task<IActionResult> RegistrarDiagnostico(Guid id, [FromBody] RegistrarDiagnosticoRequest req, CancellationToken ct) => Ok(await useCases.RegistrarDiagnostico(id, req, ct));

    [HttpGet("{id:guid}/status")]
    public async Task<IActionResult> ObterStatus(Guid id, CancellationToken ct) => Ok(await useCases.Status(id, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> ObterPorId(Guid id, CancellationToken ct) => Ok(await useCases.Obter(id, ct));

    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) => Ok(await useCases.Listar(ct));

    [HttpPost("{id:guid}/finalizar")]
    public async Task<IActionResult> Finalizar(Guid id, CancellationToken ct) { await useCases.Finalizar(id, ct); return NoContent(); }

    [HttpPost("{id:guid}/entregar")]
    public async Task<IActionResult> Entregar(Guid id, CancellationToken ct) { await useCases.Entregar(id, ct); return NoContent(); }
}

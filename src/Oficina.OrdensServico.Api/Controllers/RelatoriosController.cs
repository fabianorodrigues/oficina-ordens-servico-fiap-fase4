using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.OrdensServico.Api.Security;
using Oficina.OrdensServico.Application.UseCases;

namespace Oficina.OrdensServico.Api.Controllers;

[ApiController]
[Route("api/relatorios")]
[Authorize(Policy = Policies.FuncionarioOuAdmin)]
public class RelatoriosController(OrdensUseCases useCases) : ControllerBase
{
    [HttpGet("tempo-medio-execucao")]
    public async Task<IActionResult> TempoMedioExecucao(CancellationToken ct) => Ok(await useCases.Relatorio(ct));
}

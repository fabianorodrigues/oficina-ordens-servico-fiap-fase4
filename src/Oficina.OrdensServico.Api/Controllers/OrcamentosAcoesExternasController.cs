using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.OrdensServico.Application.Contracts;
using Oficina.OrdensServico.Application.UseCases;

namespace Oficina.OrdensServico.Api.Controllers;

[ApiController]
[Route("api/orcamentos/acoes-externas")]
[AllowAnonymous]
public class OrcamentosAcoesExternasController(OrdensUseCases useCases) : ControllerBase
{
    [HttpGet("aprovar")]
    public Task<IActionResult> Aprovar([FromQuery] string token, CancellationToken ct)
        => Processar(token, AcaoExternaOrcamento.Aprovar, ct);

    [HttpGet("recusar")]
    public Task<IActionResult> Recusar([FromQuery] string token, CancellationToken ct)
        => Processar(token, AcaoExternaOrcamento.Recusar, ct);

    private async Task<IActionResult> Processar(string token, AcaoExternaOrcamento acao, CancellationToken ct)
    {
        var resultado = await useCases.ProcessarAcaoExterna(new ProcessarAcaoExternaOrcamentoRequest { Token = token, Acao = acao }, ct);
        if (resultado.Sucesso) return Ok(resultado);
        return resultado.Codigo switch
        {
            "token_invalido" => BadRequest(resultado),
            "link_invalido" => NotFound(resultado),
            "link_expirado" => StatusCode(StatusCodes.Status410Gone, resultado),
            "acao_ja_processada" => Conflict(resultado),
            _ => BadRequest(resultado)
        };
    }
}

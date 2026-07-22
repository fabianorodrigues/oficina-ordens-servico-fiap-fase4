using Oficina.OrdensServico.Application.Contracts;

namespace Oficina.OrdensServico.Application.Abstractions;

public interface ICadastroClient
{
    Task<ClienteDto?> ObterCliente(Guid id, CancellationToken ct);
    Task<VeiculoDto?> ObterVeiculo(Guid id, CancellationToken ct);
    Task<ClienteDto?> ObterClientePorDocumento(string documento, CancellationToken ct);
    Task<VeiculoDto?> ObterVeiculoPorPlaca(string placa, CancellationToken ct);
    Task<ConsultaServicosResponse> ConsultarServicos(ConsultaServicosRequest request, CancellationToken ct);
}

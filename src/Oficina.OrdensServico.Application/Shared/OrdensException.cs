namespace Oficina.OrdensServico.Application.Shared;

public sealed class OrdensException(string message, int statusCode, string code = "domain_error") : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

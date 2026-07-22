namespace Oficina.OrdensServico.Api.Security;

public static class Policies
{
    public const string ClienteOnly = "ClienteOnly";
    public const string FuncionarioOuAdmin = "FuncionarioOuAdmin";
    public const string AdminOnly = "AdminOnly";
    public const string ClienteOuAdmin = "ClienteOuAdmin";
}

public static class PerfisAcesso
{
    public const string Cliente = "Cliente";
    public const string Funcionario = "Funcionario";
    public const string Admin = "Admin";
}

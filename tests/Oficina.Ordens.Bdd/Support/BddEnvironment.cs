using System.Net.Http.Headers;
using Microsoft.Data.SqlClient;

namespace Oficina.Ordens.Bdd.Support;

/// <summary>
/// Endereco dos servicos e do banco levantados por docker-compose.bdd.yml.
/// Todos os valores vem de variaveis de ambiente escritas por run-bdd.ps1:
/// nenhum endereco, porta ou senha fica embutido no codigo.
/// </summary>
public static class BddEnvironment
{
    public static string CadastroBaseUrl => Required("BDD_CADASTRO_URL");
    public static string EstoqueBaseUrl => Required("BDD_ESTOQUE_URL");
    public static string OrdensBaseUrl => Required("BDD_ORDENS_URL");
    public static string OrdensConnectionString => Required("BDD_ORDENS_CONNECTION_STRING");

    /// <summary>
    /// Timeout maximo de cada espera assincrona. Nomeado por cenario para que
    /// uma falha diga qual etapa do fluxo distribuido nao completou, em vez de
    /// apenas "o teste demorou".
    /// </summary>
    public static TimeSpan StepTimeout => TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("BDD_STEP_TIMEOUT_SECONDS"), out var seconds) ? seconds : 120);

    /// <summary>Intervalo curto de polling. Nunca um sleep fixo longo.</summary>
    public static TimeSpan PollInterval => TimeSpan.FromMilliseconds(500);

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Variavel de ambiente {name} ausente. Execute o BDD por scripts/run-bdd.ps1.");
        }

        return value;
    }

    public static HttpClient CreateClient(string baseUrl, string correlationId)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // O ambiente do BDD roda com Authentication__Mode=Development, que
        // aceita a identidade por cabecalho. Nenhuma credencial real e usada.
        client.DefaultRequestHeaders.Add("X-Dev-Role", "Funcionario");
        client.DefaultRequestHeaders.Add("X-Dev-Cpf", "12345678901");
        client.DefaultRequestHeaders.Add("X-Dev-FuncionarioId", "11111111-1111-1111-1111-111111111111");
        client.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);
        return client;
    }

    public static async Task<T?> QueryScalarAsync<T>(string sql, IDictionary<string, object> parameters, CancellationToken ct)
    {
        await using var connection = new SqlConnection(OrdensConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        }

        var result = await command.ExecuteScalarAsync(ct);
        if (result is null || result is DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(result, typeof(T));
    }

    /// <summary>
    /// Espera ativa com intervalo curto. A mensagem de falha carrega o nome da
    /// etapa e o ultimo valor observado, para que o diagnostico nao dependa dos
    /// logs dos containers.
    /// </summary>
    public static async Task WaitUntilAsync(
        string stepName,
        Func<CancellationToken, Task<bool>> condition,
        Func<CancellationToken, Task<string>>? describeLastState = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(StepTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition(ct))
            {
                return;
            }

            await Task.Delay(PollInterval, ct);
        }

        var lastState = describeLastState is null ? "(nao informado)" : await describeLastState(ct);
        throw new TimeoutException(
            $"A etapa '{stepName}' nao completou em {StepTimeout.TotalSeconds:F0}s. Ultimo estado observado: {lastState}");
    }
}

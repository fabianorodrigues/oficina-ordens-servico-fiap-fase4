using System.Text.RegularExpressions;

namespace Oficina.OrdensServico.Api.Observability;

/// <summary>
/// Redacao complementar a allowlist de atributos estruturados.
/// A allowlist protege o que vem de scope e de state, mas o texto da mensagem e
/// o texto da excecao nao passam por ela: um template ja existente como
/// LogError("Falha em {ConnectionString}", cs) colocaria o segredo em message.
/// </summary>
internal static class LogSanitizer
{
    internal const int MaxMessageLength = 2048;
    internal const int MaxStackTraceLength = 8192;

    private const string Mask = "***";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex[] Patterns =
    [
        // Pares chave=valor de connection string. O valor termina no separador,
        // e nao no fim da linha, para nao mascarar o restante da mensagem.
        new(@"(?<key>password|pwd|user\s*id|uid|data\s*source|server|initial\s*catalog)(?<sep>\s*=\s*)(?<value>[^;""',\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout),
        new(@"(?<key>authorization)(?<sep>\s*[:=]\s*)(?<value>[^\s;""',\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout),
        new(@"(?<key>bearer)(?<sep>\s+)(?<value>[A-Za-z0-9\-._~+/]+=*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout),
        // Chaves da New Relic e tokens da AWS aparecem sem chave associada.
        new(@"NRAK-[A-Za-z0-9]{10,}", RegexOptions.CultureInvariant, MatchTimeout),
        new(@"NRAA-[A-Za-z0-9]{10,}", RegexOptions.CultureInvariant, MatchTimeout),
        new(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant, MatchTimeout),
        // JWT completo: tres segmentos base64url comecando por eyJ.
        new(@"eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant, MatchTimeout)
    ];

    public static string? Sanitize(string? value, int maxLength = MaxMessageLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = value;
        foreach (var pattern in Patterns)
        {
            try
            {
                result = pattern.Replace(result, ReplaceMatch);
            }
            catch (RegexMatchTimeoutException)
            {
                // Uma mensagem que estoura o timeout do regex nao pode ser
                // publicada sem redacao: descartar o texto e preferivel.
                return Mask;
            }
        }

        return Truncate(result, maxLength);
    }

    public static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength), "...[truncated]");
    }

    private static string ReplaceMatch(Match match)
    {
        if (!match.Groups["key"].Success)
        {
            return Mask;
        }

        var separator = match.Groups["sep"].Success ? match.Groups["sep"].Value : "=";
        return string.Concat(match.Groups["key"].Value, separator, Mask);
    }
}

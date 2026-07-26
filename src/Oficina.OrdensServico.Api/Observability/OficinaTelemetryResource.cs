namespace Oficina.OrdensServico.Api.Observability;

/// <summary>
/// Fonte unica dos atributos de identidade do servico.
/// service.name usa o nome padrao do codigo, salvo override explicito por
/// OTEL_SERVICE_NAME. service.version vem de OTEL_SERVICE_VERSION e
/// deployment.environment e lido de OTEL_RESOURCE_ATTRIBUTES.
/// </summary>
internal sealed record OficinaTelemetryResource(
    string ServiceName,
    string? ServiceVersion,
    string? DeploymentEnvironment)
{
    public const string ServiceNameVariable = "OTEL_SERVICE_NAME";
    public const string ServiceVersionVariable = "OTEL_SERVICE_VERSION";
    public const string ResourceAttributesVariable = "OTEL_RESOURCE_ATTRIBUTES";

    private const string DeploymentEnvironmentAttribute = "deployment.environment";

    public static OficinaTelemetryResource Resolve(IConfiguration configuration, string defaultServiceName)
    {
        var serviceName = configuration[ServiceNameVariable];
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = defaultServiceName;
        }

        var serviceVersion = Normalize(configuration[ServiceVersionVariable]);
        var environment = ReadAttribute(configuration[ResourceAttributesVariable], DeploymentEnvironmentAttribute);

        return new OficinaTelemetryResource(serviceName, serviceVersion, environment);
    }

    private static string? ReadAttribute(string? resourceAttributes, string attributeName)
    {
        if (string.IsNullOrWhiteSpace(resourceAttributes))
        {
            return null;
        }

        foreach (var entry in resourceAttributes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = entry[..separator].Trim();
            if (string.Equals(key, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                return Normalize(entry[(separator + 1)..]);
            }
        }

        return null;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

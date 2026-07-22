namespace Oficina.EndToEndTests;

public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_E2E"), "true", StringComparison.OrdinalIgnoreCase))
            Skip = "E2E desabilitado. Defina RUN_E2E=true para executar contra Docker Compose.";
    }
}

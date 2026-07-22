using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Oficina.OrdensServico.Infrastructure.Persistencia;

public sealed class OrdensServicoDesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrdensServicoDbContext>
{
    public OrdensServicoDbContext CreateDbContext(string[] args)
    {
        var connectionString = GetConnectionString(args)
            ?? "Server=(localdb)\\mssqllocaldb;Database=OficinaOrdensServicoDb;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<OrdensServicoDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new OrdensServicoDbContext(options);
    }

    private static string? GetConnectionString(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--connection", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            const string connectionPrefix = "--connection=";
            if (args[i].StartsWith(connectionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][connectionPrefix.Length..];
            }
        }

        return Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__OficinaOrdensServicoDb");
    }
}

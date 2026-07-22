using Microsoft.EntityFrameworkCore;
using Oficina.OrdensServico.Domain.Oficina;
using Oficina.OrdensServico.Infrastructure.Persistencia;

namespace Oficina.OrdensServico.IntegrationTests;

public sealed class PersistenceMetadataTests
{
    [Fact]
    public void Modelo_configura_indices_unicos_do_orcamento()
    {
        using var db = new OrdensServicoDbContext(new DbContextOptionsBuilder<OrdensServicoDbContext>().UseSqlServer("Server=localhost;Database=OficinaOrdensServicoDb_Test;TrustServerCertificate=True").Options);
        var entity = db.Model.FindEntityType(typeof(Orcamento))!;
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.Properties.Single().Name == nameof(Orcamento.OrdemServicoId));
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.Properties.Single().Name == nameof(Orcamento.TokenAcaoExterna));
    }
}

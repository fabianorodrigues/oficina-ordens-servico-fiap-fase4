using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Oficina.OrdensServico.Infrastructure.Persistencia;

#nullable disable

namespace Oficina.OrdensServico.Infrastructure.Migrations;

[DbContext(typeof(OrdensServicoDbContext))]
partial class OrdensServicoDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.7");
    }
}

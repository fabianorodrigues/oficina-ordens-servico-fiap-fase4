using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Oficina.OrdensServico.Infrastructure.Persistencia;

#nullable disable

namespace Oficina.OrdensServico.Infrastructure.Migrations;

[DbContext(typeof(OrdensServicoDbContext))]
[Migration("20260713000000_AddSagaSnapshots")]
public partial class AddSagaSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SagaSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SagaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrdemServicoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PreviousState = table.Column<int>(type: "int", nullable: false),
                NewState = table.Column<int>(type: "int", nullable: false),
                EventType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                TriggerMessageId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                PayloadSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SagaSnapshots", x => x.Id));

        migrationBuilder.CreateIndex("IX_SagaSnapshots_SagaId", "SagaSnapshots", "SagaId");
        migrationBuilder.CreateIndex("IX_SagaSnapshots_OrdemServicoId_OccurredAtUtc", "SagaSnapshots", new[] { "OrdemServicoId", "OccurredAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("SagaSnapshots");
    }
}

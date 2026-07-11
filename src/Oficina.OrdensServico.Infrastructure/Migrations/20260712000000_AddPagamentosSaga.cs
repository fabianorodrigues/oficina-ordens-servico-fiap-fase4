using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Oficina.OrdensServico.Infrastructure.Persistencia;

#nullable disable

namespace Oficina.OrdensServico.Infrastructure.Migrations;

[DbContext(typeof(OrdensServicoDbContext))]
[Migration("20260712000000_AddPagamentosSaga")]
public partial class AddPagamentosSaga : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Pagamentos",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrdemServicoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PagamentoExternoId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                ChaveIdempotencia = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                AttemptCount = table.Column<int>(type: "int", nullable: false),
                NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                LockedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                LockedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Pagamentos", x => x.Id));

        migrationBuilder.CreateTable(
            name: "SagasOrdensServico",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrdemServicoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                ReservaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SagasOrdensServico", x => x.Id));

        migrationBuilder.CreateIndex("IX_Pagamentos_ChaveIdempotencia", "Pagamentos", "ChaveIdempotencia", unique: true);
        migrationBuilder.CreateIndex("IX_Pagamentos_OrdemServicoId", "Pagamentos", "OrdemServicoId");
        migrationBuilder.CreateIndex("IX_Pagamentos_Status_NextAttemptAtUtc_LockedUntilUtc", "Pagamentos", new[] { "Status", "NextAttemptAtUtc", "LockedUntilUtc" });
        migrationBuilder.CreateIndex("IX_SagasOrdensServico_OrdemServicoId", "SagasOrdensServico", "OrdemServicoId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Pagamentos");
        migrationBuilder.DropTable("SagasOrdensServico");
    }
}

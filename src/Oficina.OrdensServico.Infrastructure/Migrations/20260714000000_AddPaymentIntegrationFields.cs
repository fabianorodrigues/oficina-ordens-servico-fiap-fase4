using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Oficina.OrdensServico.Infrastructure.Persistencia;

#nullable disable

namespace Oficina.OrdensServico.Infrastructure.Migrations;

[DbContext(typeof(OrdensServicoDbContext))]
[Migration("20260714000000_AddPaymentIntegrationFields")]
public partial class AddPaymentIntegrationFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Provider",
            table: "Pagamentos",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "Mock");

        migrationBuilder.AddColumn<string>(
            name: "OperationType",
            table: "Pagamentos",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "Payment");

        migrationBuilder.AddColumn<string>(
            name: "CompensacaoExternaId",
            table: "Pagamentos",
            type: "nvarchar(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "CompensatedAtUtc",
            table: "Pagamentos",
            type: "datetimeoffset",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("CompensatedAtUtc", "Pagamentos");
        migrationBuilder.DropColumn("CompensacaoExternaId", "Pagamentos");
        migrationBuilder.DropColumn("OperationType", "Pagamentos");
        migrationBuilder.DropColumn("Provider", "Pagamentos");
    }
}

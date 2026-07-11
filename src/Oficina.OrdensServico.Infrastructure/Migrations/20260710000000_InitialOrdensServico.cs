using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Oficina.OrdensServico.Infrastructure.Persistencia;

#nullable disable

namespace Oficina.OrdensServico.Infrastructure.Migrations;

[DbContext(typeof(OrdensServicoDbContext))]
[Migration("20260710000000_InitialOrdensServico")]
public partial class InitialOrdensServico : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("OrdensServico", table => new
        {
            Id = table.Column<Guid>("uniqueidentifier", nullable: false),
            ClienteId = table.Column<Guid>("uniqueidentifier", nullable: false),
            VeiculoId = table.Column<Guid>("uniqueidentifier", nullable: false),
            TipoManutencao = table.Column<int>("int", nullable: false),
            Status = table.Column<int>("int", nullable: false),
            OrigemUltimaAtualizacaoStatus = table.Column<int>("int", nullable: false),
            DataUltimaAtualizacaoStatus = table.Column<DateTimeOffset>("datetimeoffset", nullable: false),
            DataCriacao = table.Column<DateTimeOffset>("datetimeoffset", nullable: false),
            DataInicioExecucao = table.Column<DateTimeOffset>("datetimeoffset", nullable: true),
            DataFimExecucao = table.Column<DateTimeOffset>("datetimeoffset", nullable: true),
            OrcamentoId = table.Column<Guid>("uniqueidentifier", nullable: true),
            ClienteSnapshotId = table.Column<Guid>("uniqueidentifier", nullable: false),
            Nome = table.Column<string>("nvarchar(200)", maxLength: 200, nullable: false),
            Documento = table.Column<string>("nvarchar(32)", maxLength: 32, nullable: false),
            Email = table.Column<string>("nvarchar(200)", maxLength: 200, nullable: false),
            Telefone = table.Column<string>("nvarchar(30)", maxLength: 30, nullable: false),
            VeiculoSnapshotId = table.Column<Guid>("uniqueidentifier", nullable: false),
            Placa = table.Column<string>("nvarchar(10)", maxLength: 10, nullable: false),
            Renavam = table.Column<string>("nvarchar(20)", maxLength: 20, nullable: false),
            ModeloDescricao = table.Column<string>("nvarchar(120)", maxLength: 120, nullable: false),
            Marca = table.Column<string>("nvarchar(80)", maxLength: 80, nullable: false),
            Ano = table.Column<int>("int", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_OrdensServico", x => x.Id));

        migrationBuilder.CreateTable("Orcamentos", table => new
        {
            Id = table.Column<Guid>("uniqueidentifier", nullable: false),
            OrdemServicoId = table.Column<Guid>("uniqueidentifier", nullable: false),
            Status = table.Column<int>("int", nullable: false),
            ValorTotal = table.Column<decimal>("decimal(18,2)", nullable: false),
            DataCriacao = table.Column<DateTimeOffset>("datetimeoffset", nullable: false),
            TokenAcaoExterna = table.Column<string>("nvarchar(200)", maxLength: 200, nullable: true),
            TokenAcaoExternaExpiraEm = table.Column<DateTimeOffset>("datetimeoffset", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_Orcamentos", x => x.Id));

        migrationBuilder.CreateTable("Diagnosticos", table => new
        {
            OrdemServicoId = table.Column<Guid>("uniqueidentifier", nullable: false),
            Descricao = table.Column<string>("nvarchar(2000)", maxLength: 2000, nullable: false),
            DataRegistro = table.Column<DateTimeOffset>("datetimeoffset", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Diagnosticos", x => x.OrdemServicoId);
            table.ForeignKey("FK_Diagnosticos_OrdensServico_OrdemServicoId", x => x.OrdemServicoId, "OrdensServico", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable("ItensServicoOs", table => new
        {
            Id = table.Column<Guid>("uniqueidentifier", nullable: false),
            ServicoId = table.Column<Guid>("uniqueidentifier", nullable: false),
            OrdemServicoId = table.Column<Guid>("uniqueidentifier", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ItensServicoOs", x => x.Id);
            table.ForeignKey("FK_ItensServicoOs_OrdensServico_OrdemServicoId", x => x.OrdemServicoId, "OrdensServico", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable("OrcamentoItensServico", table => new
        {
            Id = table.Column<Guid>("uniqueidentifier", nullable: false),
            ServicoId = table.Column<Guid>("uniqueidentifier", nullable: false),
            ValorMaoDeObra = table.Column<decimal>("decimal(18,2)", nullable: false),
            DescricaoSnapshot = table.Column<string>("nvarchar(200)", maxLength: 200, nullable: false),
            OrcamentoId = table.Column<Guid>("uniqueidentifier", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_OrcamentoItensServico", x => x.Id);
            table.ForeignKey("FK_OrcamentoItensServico_Orcamentos_OrcamentoId", x => x.OrcamentoId, "Orcamentos", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable("OrcamentoItensMaterial", table => new
        {
            Id = table.Column<Guid>("uniqueidentifier", nullable: false),
            Tipo = table.Column<int>("int", nullable: false),
            MaterialId = table.Column<Guid>("uniqueidentifier", nullable: false),
            Quantidade = table.Column<int>("int", nullable: false),
            ValorUnitario = table.Column<decimal>("decimal(18,2)", nullable: false),
            ValorTotal = table.Column<decimal>("decimal(18,2)", nullable: false),
            DescricaoSnapshot = table.Column<string>("nvarchar(200)", maxLength: 200, nullable: false),
            OrcamentoId = table.Column<Guid>("uniqueidentifier", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_OrcamentoItensMaterial", x => x.Id);
            table.ForeignKey("FK_OrcamentoItensMaterial_Orcamentos_OrcamentoId", x => x.OrcamentoId, "Orcamentos", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateIndex("IX_ItensServicoOs_OrdemServicoId", "ItensServicoOs", "OrdemServicoId");
        migrationBuilder.CreateIndex("IX_Orcamentos_OrdemServicoId", "Orcamentos", "OrdemServicoId", unique: true);
        migrationBuilder.CreateIndex("IX_Orcamentos_TokenAcaoExterna", "Orcamentos", "TokenAcaoExterna", unique: true, filter: "[TokenAcaoExterna] IS NOT NULL");
        migrationBuilder.CreateIndex("IX_OrcamentoItensMaterial_OrcamentoId", "OrcamentoItensMaterial", "OrcamentoId");
        migrationBuilder.CreateIndex("IX_OrcamentoItensMaterial_Tipo_MaterialId", "OrcamentoItensMaterial", new[] { "Tipo", "MaterialId" });
        migrationBuilder.CreateIndex("IX_OrcamentoItensServico_OrcamentoId", "OrcamentoItensServico", "OrcamentoId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Diagnosticos");
        migrationBuilder.DropTable("ItensServicoOs");
        migrationBuilder.DropTable("OrcamentoItensMaterial");
        migrationBuilder.DropTable("OrcamentoItensServico");
        migrationBuilder.DropTable("OrdensServico");
        migrationBuilder.DropTable("Orcamentos");
    }
}

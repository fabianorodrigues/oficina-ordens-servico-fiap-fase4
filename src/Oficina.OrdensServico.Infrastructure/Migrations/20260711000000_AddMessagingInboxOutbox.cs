using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Oficina.OrdensServico.Infrastructure.Persistencia;

#nullable disable

namespace Oficina.OrdensServico.Infrastructure.Migrations;

[DbContext(typeof(OrdensServicoDbContext))]
[Migration("20260711000000_AddMessagingInboxOutbox")]
public partial class AddMessagingInboxOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InboxMessages",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MessageType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                OrdemServicoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CorrelationId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                Attempts = table.Column<int>(type: "int", nullable: false),
                ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                LockedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ProcessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_InboxMessages", x => x.Id));

        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MessageType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                OrdemServicoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CorrelationId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                CausationId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Attempts = table.Column<int>(type: "int", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                LockedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_OutboxMessages", x => x.Id));

        migrationBuilder.CreateIndex("IX_InboxMessages_MessageId", "InboxMessages", "MessageId", unique: true);
        migrationBuilder.CreateIndex("IX_InboxMessages_Status_LockedUntilUtc_ReceivedAtUtc", "InboxMessages", new[] { "Status", "LockedUntilUtc", "ReceivedAtUtc" });
        migrationBuilder.CreateIndex("IX_OutboxMessages_MessageId", "OutboxMessages", "MessageId", unique: true);
        migrationBuilder.CreateIndex("IX_OutboxMessages_PublishedAtUtc_LockedUntilUtc_CreatedAtUtc", "OutboxMessages", new[] { "PublishedAtUtc", "LockedUntilUtc", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("InboxMessages");
        migrationBuilder.DropTable("OutboxMessages");
    }
}

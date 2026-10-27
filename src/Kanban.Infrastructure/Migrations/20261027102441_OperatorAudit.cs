using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OperatorAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ActorId",
                schema: "app",
                table: "AuditEvent",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "OperatorObjectId",
                schema: "app",
                table: "AuditEvent",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Audit_Actor",
                schema: "app",
                table: "AuditEvent",
                sql: "([ActorId] IS NOT NULL AND [OperatorObjectId] IS NULL) OR ([ActorId] IS NULL AND [OperatorObjectId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Audit_Actor",
                schema: "app",
                table: "AuditEvent");

            migrationBuilder.DropColumn(
                name: "OperatorObjectId",
                schema: "app",
                table: "AuditEvent");

            migrationBuilder.AlterColumn<Guid>(
                name: "ActorId",
                schema: "app",
                table: "AuditEvent",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}

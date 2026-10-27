using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BusinessFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.EnsureSchema(
                name: "bootstrap");

            migrationBuilder.CreateTable(
                name: "ClientRegistration",
                schema: "bootstrap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientRegistration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Company",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Company", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_Company_Id", x => x.Id);
                    table.CheckConstraint("CK_Company_Ownership", "[CompanyId] = [Id]");
                });

            migrationBuilder.CreateTable(
                name: "Employee",
                schema: "bootstrap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DirectoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employee", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientApproval",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientApproval", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_ClientApproval_ClientRegistration_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "bootstrap",
                        principalTable: "ClientRegistration",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClientApproval_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CompanyGroup",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyGroup", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_CompanyGroup_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Team",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Team", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Team_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CompanyCreationReceipt",
                schema: "bootstrap",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyCreationReceipt", x => new { x.UserId, x.Key });
                    table.ForeignKey(
                        name: "FK_CompanyCreationReceipt_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompanyCreationReceipt_Employee_UserId",
                        column: x => x.UserId,
                        principalSchema: "bootstrap",
                        principalTable: "Employee",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Membership",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Administrator = table.Column<bool>(type: "bit", nullable: false),
                    Epoch = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Membership", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Membership_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Membership_Employee_UserId",
                        column: x => x.UserId,
                        principalSchema: "bootstrap",
                        principalTable: "Employee",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Board",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Board", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_Board_CompanyId_TeamId_Id", x => new { x.CompanyId, x.TeamId, x.Id });
                    table.ForeignKey(
                        name: "FK_Board_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Board_Team_CompanyId_TeamId",
                        columns: x => new { x.CompanyId, x.TeamId },
                        principalSchema: "app",
                        principalTable: "Team",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "GroupGrant",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupGrant", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_GroupGrant_Role", "[Role] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_GroupGrant_CompanyGroup_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "app",
                        principalTable: "CompanyGroup",
                        principalColumns: new[] { "CompanyId", "Id" });
                    table.ForeignKey(
                        name: "FK_GroupGrant_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GroupGrant_Team_CompanyId_TeamId",
                        columns: x => new { x.CompanyId, x.TeamId },
                        principalSchema: "app",
                        principalTable: "Team",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "AuditEvent",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvent", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_AuditEvent_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AuditEvent_Membership_CompanyId_ActorId",
                        columns: x => new { x.CompanyId, x.ActorId },
                        principalSchema: "app",
                        principalTable: "Membership",
                        principalColumns: new[] { "CompanyId", "Id" });
                    table.ForeignKey(
                        name: "FK_AuditEvent_Team_CompanyId_TeamId",
                        columns: x => new { x.CompanyId, x.TeamId },
                        principalSchema: "app",
                        principalTable: "Team",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "ConnectionGrant",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipEpoch = table.Column<int>(type: "int", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Read = table.Column<bool>(type: "bit", nullable: false),
                    Write = table.Column<bool>(type: "bit", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionGrant", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_ConnectionGrant_ClientRegistration_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "bootstrap",
                        principalTable: "ClientRegistration",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectionGrant_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectionGrant_Membership_CompanyId_MembershipId",
                        columns: x => new { x.CompanyId, x.MembershipId },
                        principalSchema: "app",
                        principalTable: "Membership",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "GroupMember",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipEpoch = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMember", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_GroupMember_CompanyGroup_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "app",
                        principalTable: "CompanyGroup",
                        principalColumns: new[] { "CompanyId", "Id" });
                    table.ForeignKey(
                        name: "FK_GroupMember_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GroupMember_Membership_CompanyId_MembershipId",
                        columns: x => new { x.CompanyId, x.MembershipId },
                        principalSchema: "app",
                        principalTable: "Membership",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "Invitation",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Revoked = table.Column<bool>(type: "bit", nullable: false),
                    Consumed = table.Column<bool>(type: "bit", nullable: false),
                    InvitedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitation", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Invitation_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Invitation_Membership_CompanyId_InvitedBy",
                        columns: x => new { x.CompanyId, x.InvitedBy },
                        principalSchema: "app",
                        principalTable: "Membership",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "MemberGrant",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipEpoch = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberGrant", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_MemberGrant_Role", "[Role] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_MemberGrant_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MemberGrant_Membership_CompanyId_MembershipId",
                        columns: x => new { x.CompanyId, x.MembershipId },
                        principalSchema: "app",
                        principalTable: "Membership",
                        principalColumns: new[] { "CompanyId", "Id" });
                    table.ForeignKey(
                        name: "FK_MemberGrant_Team_CompanyId_TeamId",
                        columns: x => new { x.CompanyId, x.TeamId },
                        principalSchema: "app",
                        principalTable: "Team",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "MutationReceipt",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Operation = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    Key = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultVersion = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MutationReceipt", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_MutationReceipt_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MutationReceipt_Membership_CompanyId_MembershipId",
                        columns: x => new { x.CompanyId, x.MembershipId },
                        principalSchema: "app",
                        principalTable: "Membership",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "WorkItem",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BoardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AssigneeMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItem", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Task_Status", "[Status] IN ('todo','inProgress','done')");
                    table.ForeignKey(
                        name: "FK_WorkItem_Board_CompanyId_TeamId_BoardId",
                        columns: x => new { x.CompanyId, x.TeamId, x.BoardId },
                        principalSchema: "app",
                        principalTable: "Board",
                        principalColumns: new[] { "CompanyId", "TeamId", "Id" });
                    table.ForeignKey(
                        name: "FK_WorkItem_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkItem_Membership_CompanyId_AssigneeMembershipId",
                        columns: x => new { x.CompanyId, x.AssigneeMembershipId },
                        principalSchema: "app",
                        principalTable: "Membership",
                        principalColumns: new[] { "CompanyId", "Id" });
                    table.ForeignKey(
                        name: "FK_WorkItem_Membership_CompanyId_CreatedBy",
                        columns: x => new { x.CompanyId, x.CreatedBy },
                        principalSchema: "app",
                        principalTable: "Membership",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "ConnectionTeam",
                schema: "app",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionTeam", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_ConnectionTeam_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "app",
                        principalTable: "Company",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectionTeam_ConnectionGrant_CompanyId_ConnectionId",
                        columns: x => new { x.CompanyId, x.ConnectionId },
                        principalSchema: "app",
                        principalTable: "ConnectionGrant",
                        principalColumns: new[] { "CompanyId", "Id" });
                    table.ForeignKey(
                        name: "FK_ConnectionTeam_Team_CompanyId_TeamId",
                        columns: x => new { x.CompanyId, x.TeamId },
                        principalSchema: "app",
                        principalTable: "Team",
                        principalColumns: new[] { "CompanyId", "Id" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_CompanyId_ActorId",
                schema: "app",
                table: "AuditEvent",
                columns: new[] { "CompanyId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_CompanyId_CreatedAt_Id",
                schema: "app",
                table: "AuditEvent",
                columns: new[] { "CompanyId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_CompanyId_TeamId",
                schema: "app",
                table: "AuditEvent",
                columns: new[] { "CompanyId", "TeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_Board_CompanyId_TeamId",
                schema: "app",
                table: "Board",
                columns: new[] { "CompanyId", "TeamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientApproval_ClientId",
                schema: "app",
                table: "ClientApproval",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientApproval_CompanyId_ClientId",
                schema: "app",
                table: "ClientApproval",
                columns: new[] { "CompanyId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyCreationReceipt_CompanyId",
                schema: "bootstrap",
                table: "CompanyCreationReceipt",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionGrant_ClientId",
                schema: "app",
                table: "ConnectionGrant",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionGrant_CompanyId_MembershipId_ClientId",
                schema: "app",
                table: "ConnectionGrant",
                columns: new[] { "CompanyId", "MembershipId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionTeam_CompanyId_ConnectionId_TeamId",
                schema: "app",
                table: "ConnectionTeam",
                columns: new[] { "CompanyId", "ConnectionId", "TeamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionTeam_CompanyId_TeamId",
                schema: "app",
                table: "ConnectionTeam",
                columns: new[] { "CompanyId", "TeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_Employee_DirectoryId_ObjectId",
                schema: "bootstrap",
                table: "Employee",
                columns: new[] { "DirectoryId", "ObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupGrant_CompanyId_GroupId",
                schema: "app",
                table: "GroupGrant",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupGrant_CompanyId_TeamId_GroupId",
                schema: "app",
                table: "GroupGrant",
                columns: new[] { "CompanyId", "TeamId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMember_CompanyId_GroupId_MembershipId",
                schema: "app",
                table: "GroupMember",
                columns: new[] { "CompanyId", "GroupId", "MembershipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMember_CompanyId_MembershipId",
                schema: "app",
                table: "GroupMember",
                columns: new[] { "CompanyId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitation_CompanyId_InvitedBy",
                schema: "app",
                table: "Invitation",
                columns: new[] { "CompanyId", "InvitedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitation_TokenHash",
                schema: "app",
                table: "Invitation",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemberGrant_CompanyId_MembershipId",
                schema: "app",
                table: "MemberGrant",
                columns: new[] { "CompanyId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_MemberGrant_CompanyId_TeamId_MembershipId",
                schema: "app",
                table: "MemberGrant",
                columns: new[] { "CompanyId", "TeamId", "MembershipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Membership_CompanyId_UserId",
                schema: "app",
                table: "Membership",
                columns: new[] { "CompanyId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Membership_UserId",
                schema: "app",
                table: "Membership",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MutationReceipt_CompanyId_MembershipId_Channel_Operation_Key",
                schema: "app",
                table: "MutationReceipt",
                columns: new[] { "CompanyId", "MembershipId", "Channel", "Operation", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItem_CompanyId_AssigneeMembershipId",
                schema: "app",
                table: "WorkItem",
                columns: new[] { "CompanyId", "AssigneeMembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItem_CompanyId_CreatedBy",
                schema: "app",
                table: "WorkItem",
                columns: new[] { "CompanyId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItem_CompanyId_TeamId_BoardId",
                schema: "app",
                table: "WorkItem",
                columns: new[] { "CompanyId", "TeamId", "BoardId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItem_CompanyId_TeamId_CreatedAt_Id",
                schema: "app",
                table: "WorkItem",
                columns: new[] { "CompanyId", "TeamId", "CreatedAt", "Id" });
            foreach (var statement in Persistence.BusinessSecurityV1.MigrationStatements())
                migrationBuilder.Sql(statement);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Persistence.BusinessSecurityV1.Tables)
                migrationBuilder.Sql($"DROP SECURITY POLICY [app].[{table}Policy];");
            migrationBuilder.Sql("""
                DROP FUNCTION [app].[DirectoryScope];
                DROP FUNCTION [app].[CompanyScope];
                DROP PROCEDURE [bootstrap].[CreateCompany];
                DROP PROCEDURE [bootstrap].[FindInvitation];
                DROP PROCEDURE [bootstrap].[ListCompanies];
                DROP PROCEDURE [bootstrap].[ResolveEmployee];
                DROP USER [kanban_directory];
                """);
            migrationBuilder.DropTable(
                name: "AuditEvent",
                schema: "app");

            migrationBuilder.DropTable(
                name: "ClientApproval",
                schema: "app");

            migrationBuilder.DropTable(
                name: "CompanyCreationReceipt",
                schema: "bootstrap");

            migrationBuilder.DropTable(
                name: "ConnectionTeam",
                schema: "app");

            migrationBuilder.DropTable(
                name: "GroupGrant",
                schema: "app");

            migrationBuilder.DropTable(
                name: "GroupMember",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Invitation",
                schema: "app");

            migrationBuilder.DropTable(
                name: "MemberGrant",
                schema: "app");

            migrationBuilder.DropTable(
                name: "MutationReceipt",
                schema: "app");

            migrationBuilder.DropTable(
                name: "WorkItem",
                schema: "app");

            migrationBuilder.DropTable(
                name: "ConnectionGrant",
                schema: "app");

            migrationBuilder.DropTable(
                name: "CompanyGroup",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Board",
                schema: "app");

            migrationBuilder.DropTable(
                name: "ClientRegistration",
                schema: "bootstrap");

            migrationBuilder.DropTable(
                name: "Membership",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Team",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Employee",
                schema: "bootstrap");

            migrationBuilder.DropTable(
                name: "Company",
                schema: "app");
        }
    }
}

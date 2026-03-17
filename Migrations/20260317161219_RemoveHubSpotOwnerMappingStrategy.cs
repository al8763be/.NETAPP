using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication2.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHubSpotOwnerMappingStrategy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HubSpotDealImports_HubSpotOwnerMappings_HubSpotOwnerId",
                table: "HubSpotDealImports");

            migrationBuilder.DropTable(
                name: "HubSpotOwnerMappings");

            migrationBuilder.DropIndex(
                name: "IX_HubSpotDealImports_HubSpotOwnerId_FulfilledDateUtc",
                table: "HubSpotDealImports");

            migrationBuilder.DropColumn(
                name: "HubSpotOwnerId",
                table: "HubSpotDealImports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HubSpotOwnerId",
                table: "HubSpotDealImports",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HubSpotOwnerMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    HubSpotFirstName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HubSpotLastName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HubSpotOwnerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    HubSpotOwnerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HubSpotPrimaryTeamName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HubSpotTeamNames = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    LastOwnerSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OwnerUsername = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubSpotOwnerMappings", x => x.Id);
                    table.UniqueConstraint("AK_HubSpotOwnerMappings_HubSpotOwnerId", x => x.HubSpotOwnerId);
                    table.ForeignKey(
                        name: "FK_HubSpotOwnerMappings_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotDealImports_HubSpotOwnerId_FulfilledDateUtc",
                table: "HubSpotDealImports",
                columns: new[] { "HubSpotOwnerId", "FulfilledDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotOwnerMappings_HubSpotOwnerId",
                table: "HubSpotOwnerMappings",
                column: "HubSpotOwnerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotOwnerMappings_OwnerUserId",
                table: "HubSpotOwnerMappings",
                column: "OwnerUserId",
                unique: true,
                filter: "[OwnerUserId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_HubSpotDealImports_HubSpotOwnerMappings_HubSpotOwnerId",
                table: "HubSpotDealImports",
                column: "HubSpotOwnerId",
                principalTable: "HubSpotOwnerMappings",
                principalColumn: "HubSpotOwnerId");
        }
    }
}

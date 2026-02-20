using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication2.Migrations
{
    /// <inheritdoc />
    public partial class AddHubSpotTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HubSpotDealImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExternalDealId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DealName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    OwnerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    FulfilledDateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    DealStage = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HubSpotLastModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubSpotDealImports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HubSpotDealImports_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "HubSpotSyncRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DealsFetched = table.Column<int>(type: "int", nullable: false),
                    DealsImported = table.Column<int>(type: "int", nullable: false),
                    DealsUpdated = table.Column<int>(type: "int", nullable: false),
                    DealsSkipped = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubSpotSyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HubSpotSyncStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IntegrationName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastSuccessfulSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCursor = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubSpotSyncStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotDealImports_ExternalDealId",
                table: "HubSpotDealImports",
                column: "ExternalDealId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotDealImports_OwnerUserId_FulfilledDateUtc",
                table: "HubSpotDealImports",
                columns: new[] { "OwnerUserId", "FulfilledDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotSyncRuns_StartedUtc",
                table: "HubSpotSyncRuns",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotSyncStates_IntegrationName",
                table: "HubSpotSyncStates",
                column: "IntegrationName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HubSpotDealImports");

            migrationBuilder.DropTable(
                name: "HubSpotSyncRuns");

            migrationBuilder.DropTable(
                name: "HubSpotSyncStates");
        }
    }
}

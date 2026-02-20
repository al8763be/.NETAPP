using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using WebApplication2.Data;

#nullable disable

namespace WebApplication2.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(STLForumContext))]
    [Migration("20260219191500_DecoupleHubSpotLeaderboardFromUserMapping")]
    public partial class DecoupleHubSpotLeaderboardFromUserMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.key_constraints
                    WHERE [name] = N'IX_ContestEntries_ContestId_UserId'
                      AND [parent_object_id] = OBJECT_ID(N'[ContestEntries]')
                )
                BEGIN
                    ALTER TABLE [ContestEntries] DROP CONSTRAINT [IX_ContestEntries_ContestId_UserId];
                END
                """);

            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE [name] = N'IX_ContestEntries_ContestId_UserId'
                      AND [object_id] = OBJECT_ID(N'[ContestEntries]')
                )
                BEGIN
                    DROP INDEX [IX_ContestEntries_ContestId_UserId] ON [ContestEntries];
                END
                """);

            migrationBuilder.AddColumn<string>(
                name: "HubSpotOwnerId",
                table: "HubSpotDealImports",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HubSpotPrimaryTeamName",
                table: "HubSpotOwnerMappings",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HubSpotTeamNames",
                table: "HubSpotOwnerMappings",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContestEntries_ContestId_EmployeeNumber",
                table: "ContestEntries",
                columns: new[] { "ContestId", "EmployeeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotDealImports_HubSpotOwnerId_FulfilledDateUtc",
                table: "HubSpotDealImports",
                columns: new[] { "HubSpotOwnerId", "FulfilledDateUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.key_constraints
                    WHERE [name] = N'IX_ContestEntries_ContestId_EmployeeNumber'
                      AND [parent_object_id] = OBJECT_ID(N'[ContestEntries]')
                )
                BEGIN
                    ALTER TABLE [ContestEntries] DROP CONSTRAINT [IX_ContestEntries_ContestId_EmployeeNumber];
                END
                """);

            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE [name] = N'IX_ContestEntries_ContestId_EmployeeNumber'
                      AND [object_id] = OBJECT_ID(N'[ContestEntries]')
                )
                BEGIN
                    DROP INDEX [IX_ContestEntries_ContestId_EmployeeNumber] ON [ContestEntries];
                END
                """);

            migrationBuilder.DropIndex(
                name: "IX_HubSpotDealImports_HubSpotOwnerId_FulfilledDateUtc",
                table: "HubSpotDealImports");

            migrationBuilder.DropColumn(
                name: "HubSpotOwnerId",
                table: "HubSpotDealImports");

            migrationBuilder.DropColumn(
                name: "HubSpotPrimaryTeamName",
                table: "HubSpotOwnerMappings");

            migrationBuilder.DropColumn(
                name: "HubSpotTeamNames",
                table: "HubSpotOwnerMappings");

            migrationBuilder.CreateIndex(
                name: "IX_ContestEntries_ContestId_UserId",
                table: "ContestEntries",
                columns: new[] { "ContestId", "UserId" },
                unique: true,
                filter: "[UserId] IS NOT NULL");
        }
    }
}

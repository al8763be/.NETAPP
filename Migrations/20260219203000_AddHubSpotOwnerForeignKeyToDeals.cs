using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WebApplication2.Data;

#nullable disable

namespace WebApplication2.Migrations
{
    [DbContext(typeof(STLForumContext))]
    [Migration("20260219203000_AddHubSpotOwnerForeignKeyToDeals")]
    public partial class AddHubSpotOwnerForeignKeyToDeals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill owner rows for historical deals before enforcing FK.
            migrationBuilder.Sql(
                """
                INSERT INTO [HubSpotOwnerMappings]
                    ([HubSpotOwnerId], [HubSpotOwnerEmail], [IsArchived], [LastSeenUtc], [LastOwnerSyncUtc])
                SELECT DISTINCT
                    d.[HubSpotOwnerId],
                    NULLIF(d.[OwnerEmail], N''),
                    CAST(0 AS bit),
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                FROM [HubSpotDealImports] AS d
                LEFT JOIN [HubSpotOwnerMappings] AS m
                    ON m.[HubSpotOwnerId] = d.[HubSpotOwnerId]
                WHERE d.[HubSpotOwnerId] IS NOT NULL
                  AND LTRIM(RTRIM(d.[HubSpotOwnerId])) <> N''
                  AND m.[Id] IS NULL;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_HubSpotDealImports_HubSpotOwnerMappings_HubSpotOwnerId",
                table: "HubSpotDealImports",
                column: "HubSpotOwnerId",
                principalTable: "HubSpotOwnerMappings",
                principalColumn: "HubSpotOwnerId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HubSpotDealImports_HubSpotOwnerMappings_HubSpotOwnerId",
                table: "HubSpotDealImports");
        }
    }
}

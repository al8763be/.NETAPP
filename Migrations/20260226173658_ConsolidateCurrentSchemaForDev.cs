using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication2.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateCurrentSchemaForDev : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SaljId",
                table: "HubSpotDealImports",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmployeeProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Region = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HubSpotDealImports_SaljId_FulfilledDateUtc",
                table: "HubSpotDealImports",
                columns: new[] { "SaljId", "FulfilledDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeProfiles_EmployeeNumber",
                table: "EmployeeProfiles",
                column: "EmployeeNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeProfiles_UserId",
                table: "EmployeeProfiles",
                column: "UserId",
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.Sql(@"
MERGE [EmployeeProfiles] AS [Target]
USING (VALUES
    (N'3035', N'Jonas al Dakhil', N'Malmö', N'SC'),
    (N'3045', N'Max Q', N'Malmö', N'TM'),
    (N'3055', N'Elias J', N'Malmö', NULL),
    (N'3057', N'Alfons S', N'Malmö', NULL),
    (N'3058', N'Andi R', N'Malmö', NULL),
    (N'3071', N'Leonardo R', N'Malmö', NULL),
    (N'3072', N'Izabell E', N'Malmö', NULL),
    (N'3077', N'Yousef B', N'Malmö', NULL),
    (N'3078', N'Melia B', N'Malmö', NULL),
    (N'3729', N'Lucas K', N'Malmö', NULL),
    (N'3080', N'Kevin M', N'Malmö', NULL),
    (N'3082', N'Adam Al D', N'Malmö', NULL),
    (N'3083', N'Alekssandro S', N'Malmö', NULL),
    (N'4214', N'Payam S', N'Jönköping', N'SC'),
    (N'3422', N'Fillip L', N'Jönköping', NULL),
    (N'3414', N'Tarik N', N'Jönköping', NULL),
    (N'3421', N'Benjamin M', N'Jönköping', NULL),
    (N'3423', N'Selim S', N'Jönköping', NULL),
    (N'3424', N'Daudt M', N'Jönköping', NULL),
    (N'3425', N'Laurent D', N'Jönköping', NULL),
    (N'3428', N'Alexander Y', N'Jönköping', NULL),
    (N'2874', N'Linus W', N'Sundsvall', N'SC'),
    (N'2875', N'Robin S', N'Sundsvall', NULL),
    (N'4408', N'Adrian J', N'Sundsvall', NULL),
    (N'4409', N'Robert W', N'Sundsvall', NULL),
    (N'4218', N'Rinas M', N'Göteborg', N'SC'),
    (N'4230', N'Vincent B', N'Göteborg', NULL),
    (N'4600', N'Mustafa D', N'Stockholm', N'SC'),
    (N'2855', N'Mostafa J', N'Stockholm', N'TM'),
    (N'2863', N'Jack L', N'Stockholm', NULL),
    (N'2887', N'Marcelo G', N'Stockholm', NULL),
    (N'2889', N'Adam T', N'Stockholm', NULL),
    (N'2890', N'Ridwan M', N'Stockholm', NULL),
    (N'2892', N'Elvis B', N'Stockholm', NULL),
    (N'2893', N'Samuel H', N'Stockholm', NULL),
    (N'3036', N'Simon L', N'Malmö', NULL),
    (N'3012', N'Mustafa S', N'Sverige', N'UTB'),
    (N'3067', N'Yonas B', N'Sverige', N'UTB'),
    (N'3041', N'Alex Q', N'Malmö', N'UTB'),
    (N'3368', N'Dahir A', N'Malmö', N'WINBACK')
) AS [Source]([EmployeeNumber], [FullName], [Region], [Role])
ON [Target].[EmployeeNumber] = [Source].[EmployeeNumber]
WHEN MATCHED THEN
    UPDATE SET
        [FullName] = [Source].[FullName],
        [Region] = [Source].[Region],
        [Role] = [Source].[Role],
        [UpdatedUtc] = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([EmployeeNumber], [FullName], [Region], [Role], [CreatedUtc], [UpdatedUtc])
    VALUES ([Source].[EmployeeNumber], [Source].[FullName], [Source].[Region], [Source].[Role], SYSUTCDATETIME(), SYSUTCDATETIME());

UPDATE [profiles]
SET
    [profiles].[UserId] = [users].[Id],
    [profiles].[UpdatedUtc] = SYSUTCDATETIME()
FROM [EmployeeProfiles] AS [profiles]
INNER JOIN [AspNetUsers] AS [users]
    ON [users].[UserName] = [profiles].[EmployeeNumber]
WHERE [profiles].[UserId] IS NULL OR [profiles].[UserId] <> [users].[Id];
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeProfiles");

            migrationBuilder.DropIndex(
                name: "IX_HubSpotDealImports_SaljId_FulfilledDateUtc",
                table: "HubSpotDealImports");

            migrationBuilder.DropColumn(
                name: "SaljId",
                table: "HubSpotDealImports");
        }
    }
}

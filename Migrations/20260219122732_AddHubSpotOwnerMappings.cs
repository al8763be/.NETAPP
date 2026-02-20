using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication2.Migrations
{
    /// <inheritdoc />
    public partial class AddHubSpotOwnerMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HubSpotOwnerMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    HubSpotOwnerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HubSpotOwnerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    HubSpotFirstName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HubSpotLastName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastOwnerSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    OwnerUsername = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubSpotOwnerMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HubSpotOwnerMappings_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HubSpotOwnerMappings");
        }
    }
}

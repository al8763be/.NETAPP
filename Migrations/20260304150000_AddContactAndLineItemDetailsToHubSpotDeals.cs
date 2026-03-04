using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WebApplication2.Data;

#nullable disable

namespace WebApplication2.Migrations
{
    [DbContext(typeof(STLForumContext))]
    [Migration("20260304150000_AddContactAndLineItemDetailsToHubSpotDeals")]
    public partial class AddContactAndLineItemDetailsToHubSpotDeals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactFirstName",
                table: "HubSpotDealImports",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactKundstatus",
                table: "HubSpotDealImports",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPhoneNumber",
                table: "HubSpotDealImports",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LineItemsJson",
                table: "HubSpotDealImports",
                type: "nvarchar(max)",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContactFirstName",
                table: "HubSpotDealImports");

            migrationBuilder.DropColumn(
                name: "ContactKundstatus",
                table: "HubSpotDealImports");

            migrationBuilder.DropColumn(
                name: "ContactPhoneNumber",
                table: "HubSpotDealImports");

            migrationBuilder.DropColumn(
                name: "LineItemsJson",
                table: "HubSpotDealImports");
        }
    }
}

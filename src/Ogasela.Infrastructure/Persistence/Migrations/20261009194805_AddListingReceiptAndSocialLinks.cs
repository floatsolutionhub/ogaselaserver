using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogasela.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddListingReceiptAndSocialLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FacebookUrl",
                table: "Listings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasOriginalReceipt",
                table: "Listings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstagramUrl",
                table: "Listings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TikTokUrl",
                table: "Listings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeUrl",
                table: "Listings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FacebookUrl",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "HasOriginalReceipt",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "InstagramUrl",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "TikTokUrl",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "YouTubeUrl",
                table: "Listings");
        }
    }
}

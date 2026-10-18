using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogasela.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// TesterSignups becomes InterestSignups (testers + waiting list). Hand-edited from EF's
    /// drop-and-create so existing tester rows are kept: the table is renamed, the old rows become
    /// Kind = Tester / Status = New, and emails are lower-cased (keeping the earliest row if that
    /// makes two the same).
    /// </summary>
    public partial class AddInterestSignups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_TesterSignups_Email", table: "TesterSignups");
            migrationBuilder.DropPrimaryKey(name: "PK_TesterSignups", table: "TesterSignups");
            migrationBuilder.RenameTable(name: "TesterSignups", newName: "InterestSignups");
            migrationBuilder.AddPrimaryKey(name: "PK_InterestSignups", table: "InterestSignups", column: "Id");

            migrationBuilder.AddColumn<string>(
                name: "Kind", table: "InterestSignups", type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Tester");
            migrationBuilder.AddColumn<string>(
                name: "Status", table: "InterestSignups", type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "New");
            migrationBuilder.AddColumn<string>(
                name: "Phone", table: "InterestSignups", type: "character varying(20)", maxLength: 20, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "State", table: "InterestSignups", type: "character varying(100)", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "Intent", table: "InterestSignups", type: "character varying(20)", maxLength: 20, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DevicePlatform", table: "InterestSignups", type: "character varying(20)", maxLength: 20, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "Notes", table: "InterestSignups", type: "character varying(2000)", maxLength: 2000, nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentedAt", table: "InterestSignups", type: "timestamp with time zone", nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt", table: "InterestSignups", type: "timestamp with time zone", nullable: true);

            // Old rows were submitted through the same "join" form, so their signup time is their consent time.
            migrationBuilder.Sql("""
                UPDATE "InterestSignups" SET "ConsentedAt" = "CreatedAt";
                DELETE FROM "InterestSignups" a
                    USING "InterestSignups" b
                    WHERE lower(a."Email") = lower(b."Email")
                      AND (a."CreatedAt", a."Id") > (b."CreatedAt", b."Id");
                UPDATE "InterestSignups" SET "Email" = lower(trim("Email"));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_InterestSignups_CreatedAt",
                table: "InterestSignups",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InterestSignups_Kind_Email",
                table: "InterestSignups",
                columns: new[] { "Kind", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Waiting-list rows have no home in the old table.
            migrationBuilder.Sql("""DELETE FROM "InterestSignups" WHERE "Kind" <> 'Tester';""");

            migrationBuilder.DropIndex(name: "IX_InterestSignups_CreatedAt", table: "InterestSignups");
            migrationBuilder.DropIndex(name: "IX_InterestSignups_Kind_Email", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "Kind", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "Status", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "Phone", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "State", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "Intent", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "DevicePlatform", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "Notes", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "ConsentedAt", table: "InterestSignups");
            migrationBuilder.DropColumn(name: "UpdatedAt", table: "InterestSignups");

            migrationBuilder.DropPrimaryKey(name: "PK_InterestSignups", table: "InterestSignups");
            migrationBuilder.RenameTable(name: "InterestSignups", newName: "TesterSignups");
            migrationBuilder.AddPrimaryKey(name: "PK_TesterSignups", table: "TesterSignups", column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_TesterSignups_Email",
                table: "TesterSignups",
                column: "Email",
                unique: true);
        }
    }
}

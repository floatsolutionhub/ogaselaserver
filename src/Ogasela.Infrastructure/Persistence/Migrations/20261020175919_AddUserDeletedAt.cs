using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogasela.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            // Accounts deleted before this column existed: every erasure wrote a User.DataErasure audit entry.
            migrationBuilder.Sql("""
                UPDATE "Users" u
                SET "DeletedAt" = a."ErasedAt"
                FROM (
                    SELECT "TargetId", MIN("Timestamp") AS "ErasedAt"
                    FROM "AuditLogs"
                    WHERE "Action" = 'User.DataErasure' AND "TargetType" = 'User'
                    GROUP BY "TargetId"
                ) a
                WHERE u."Id" = a."TargetId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Users");
        }
    }
}

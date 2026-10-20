using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogasela.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionPromotionPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PromotionPlanId",
                table: "Transactions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromotionPlanId",
                table: "Transactions");
        }
    }
}

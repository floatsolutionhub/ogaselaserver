using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogasela.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfficePickup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OfficePickupId",
                table: "InboxNotifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOfficePickupEligible",
                table: "Categories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OfficeBranches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    State = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    OpeningHours = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    Longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeBranches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OfficePickupActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficePickupActivities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OfficePickupRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BuyerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemPriceKobo = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ServiceFeeKobo = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalKobo = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Fulfilment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DeliveryAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CancellationFeeKobo = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    RefundedKobo = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    RefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficePickupRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfficePickupRequests_OfficeBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "OfficeBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0000-0000-0000-0000-000000000001"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0000-0000-0000-0000-000000000002"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0000-0000-0000-0000-000000000003"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0000-0000-0000-0000-000000000004"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0000-0000-0000-0000-000000000005"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0000-0000-0000-0000-000000000006"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0000-0000-0000-0000-000000000007"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000001001"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000001002"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000001003"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000001004"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000001005"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000001006"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000002001"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000002002"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000002003"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000002004"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000002005"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000003001"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000003002"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000003003"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000003004"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000004001"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000004002"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000004003"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000004004"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000005001"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000005002"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000005003"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000005004"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000006001"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000006002"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000006003"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000007001"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000007002"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000007003"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000007004"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("c17a0001-0000-0000-0000-000000007005"),
                column: "IsOfficePickupEligible",
                value: false);

            migrationBuilder.CreateIndex(
                name: "IX_OfficePickupActivities_RequestId_CreatedAt",
                table: "OfficePickupActivities",
                columns: new[] { "RequestId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficePickupRequests_BranchId",
                table: "OfficePickupRequests",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficePickupRequests_BuyerUserId",
                table: "OfficePickupRequests",
                column: "BuyerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficePickupRequests_PaymentReference",
                table: "OfficePickupRequests",
                column: "PaymentReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficePickupRequests_Reference",
                table: "OfficePickupRequests",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficePickupRequests_SellerUserId",
                table: "OfficePickupRequests",
                column: "SellerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficePickupRequests_Status_CreatedAt",
                table: "OfficePickupRequests",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficePickupActivities");

            migrationBuilder.DropTable(
                name: "OfficePickupRequests");

            migrationBuilder.DropTable(
                name: "OfficeBranches");

            migrationBuilder.DropColumn(
                name: "OfficePickupId",
                table: "InboxNotifications");

            migrationBuilder.DropColumn(
                name: "IsOfficePickupEligible",
                table: "Categories");
        }
    }
}

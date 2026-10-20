using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.OfficePickups;
using Ogasela.Domain.Payments;
using Ogasela.Infrastructure.Persistence;
using Ogasela.Infrastructure.Promotions.Seed;
using Ogasela.IntegrationTests.Listings;
using static Ogasela.IntegrationTests.Listings.ListingsTestHelpers;

namespace Ogasela.IntegrationTests.Admin;

public class AdminRevenueTests : IClassFixture<ListingsApiFactory>
{
    private readonly ListingsApiFactory _factory;

    public AdminRevenueTests(ListingsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SuperAdmin_SeesEarnedRevenueSeparateFromTopUps_AndTheLedger()
    {
        var seller = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(seller, CategorySeedData.PhonesAndTabletsId);
        var me = await seller.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var userId = me.GetProperty("id").GetGuid();

        string reference;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OgaselaDbContext>();
            var sellerId = (await db.SellerProfiles.FirstAsync(s => s.UserId == userId)).Id;
            var now = DateTime.UtcNow;
            reference = $"REV-{Guid.NewGuid():N}"[..16];
            db.Transactions.Add(Transaction.CreateSucceeded(sellerId, TransactionType.AdSpendTopUp, 500_000m, reference, null, now));
            db.Transactions.Add(Transaction.CreateSucceeded(sellerId, TransactionType.PlanPurchase, 250_000m, null, listing.Id, now));
            db.Transactions.Add(Transaction.CreatePending(sellerId, TransactionType.AdSpendTopUp, 100_000m, reference + "P", now));
            await db.SaveChangesAsync();
        }

        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);
        var summary = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/revenue/summary");
        summary.GetProperty("revenueKobo").GetDecimal().Should().BeGreaterThanOrEqualTo(250_000m);
        summary.GetProperty("topUpsKobo").GetDecimal().Should().BeGreaterThanOrEqualTo(500_000m);
        summary.GetProperty("pendingTopUps").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        summary.GetProperty("bucket").GetString().Should().Be("day");
        summary.GetProperty("series").GetArrayLength().Should().Be(30);
        summary.GetProperty("topSellers").EnumerateArray().Should().Contain(s => s.GetProperty("userId").GetGuid() == userId);

        var ledger = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/admin/revenue/transactions?search={reference}");
        ledger.GetProperty("items").GetArrayLength().Should().Be(2);
        var plans = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/revenue/transactions?type=PlanPurchase&pageSize=100");
        var purchase = plans.GetProperty("items").EnumerateArray().First(i => i.GetProperty("listingId").GetGuid() == listing.Id);
        purchase.GetProperty("planName").GetString().Should().Be("Free");
        purchase.GetProperty("listingTitle").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task OfficePickupFees_CountAsRevenue_ItemPricesOnlyAsCollected()
    {
        var seller = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(seller, CategorySeedData.PhonesAndTabletsId);
        var sellerUserId = (await seller.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("id").GetGuid();

        // A month nothing else in this fixture touches, so the totals are exact.
        var day = new DateTime(2021, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OgaselaDbContext>();
            var sellerId = (await db.SellerProfiles.FirstAsync(s => s.UserId == sellerUserId)).Id;
            var branch = OfficeBranch.Create("Ikeja", "1 Allen Avenue", "Lagos", "Ikeja", null, null, 6.6m, 3.35m, day);
            db.OfficeBranches.Add(branch);

            OfficePickupRequest Pickup(decimal priceKobo) => OfficePickupRequest.Create(
                $"OP-{Guid.NewGuid():N}"[..14], listing.Id, "Phone", Guid.NewGuid(), sellerUserId, branch.Id,
                priceKobo, priceKobo * 0.03m, day);

            var completed = Pickup(1_000_000m); // ₦10,000 item, ₦300 fee
            completed.MarkPaid(day);
            completed.MoveTo(OfficePickupStatus.Completed, day.AddDays(2));
            var refunded = Pickup(2_000_000m); // ₦20,000 item; refund keeps 1% = ₦200
            refunded.MarkPaid(day);
            refunded.MarkRefunded(20_000m, 2_040_000m, day.AddDays(1));
            var unpaid = Pickup(5_000_000m);

            db.OfficePickupRequests.AddRange(completed, refunded, unpaid);
            db.Transactions.Add(Transaction.CreateSucceeded(sellerId, TransactionType.PlanPurchase, 150_000m, null, listing.Id, day));
            await db.SaveChangesAsync();
        }

        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);
        var summary = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/revenue/summary?from=2021-03-01&to=2021-03-31");

        summary.GetProperty("planRevenueKobo").GetDecimal().Should().Be(150_000m);
        summary.GetProperty("officePickupFeesKobo").GetDecimal().Should().Be(30_000m + 20_000m);
        summary.GetProperty("revenueKobo").GetDecimal().Should().Be(150_000m + 50_000m);
        summary.GetProperty("officePickups").GetInt32().Should().Be(2);
        summary.GetProperty("officePickupCollectedKobo").GetDecimal().Should().Be(1_030_000m + 2_060_000m);
        summary.GetProperty("refundsKobo").GetDecimal().Should().Be(2_040_000m);
        summary.GetProperty("refunds").GetInt32().Should().Be(1);

        var march10 = summary.GetProperty("series").EnumerateArray()
            .Single(p => p.GetProperty("date").GetDateTime().Date == day.Date);
        march10.GetProperty("revenueKobo").GetDecimal().Should().Be(200_000m);
        march10.GetProperty("officePickupFeesKobo").GetDecimal().Should().Be(50_000m);

        var april = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/revenue/summary?from=2021-04-01&to=2021-04-30");
        april.GetProperty("previousRevenueKobo").GetDecimal().Should().Be(200_000m);
        summary.GetProperty("allTimeRevenueKobo").GetDecimal().Should().BeGreaterThanOrEqualTo(200_000m);
    }

    [Fact]
    public async Task OtherStaff_CannotSeeRevenue()
    {
        var (moderator, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.Moderator);

        (await moderator.GetAsync("/api/v1/admin/revenue/summary")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

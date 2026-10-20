using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Ogasela.Domain.Accounts;
using Ogasela.Infrastructure.Promotions.Seed;
using Ogasela.IntegrationTests.Listings;
using static Ogasela.IntegrationTests.Listings.ListingsTestHelpers;

namespace Ogasela.IntegrationTests.Admin;

public class AdminPendingActionsTests : IClassFixture<ApprovalListingsApiFactory>
{
    private readonly ApprovalListingsApiFactory _factory;

    public AdminPendingActionsTests(ApprovalListingsApiFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Dictionary<string, int>> CountsAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/pending-actions");
        var items = body.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("count").GetInt32());
        body.GetProperty("total").GetInt32().Should().Be(items.Values.Sum());
        return items;
    }

    [Fact]
    public async Task SuperAdmin_SeesEveryQueue_AndANewAdAwaitingApprovalIsCounted()
    {
        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);
        var before = await CountsAsync(admin);
        before.Keys.Should().BeEquivalentTo(
            "reports", "adApprovals", "manualReview", "changeRequests", "userDocuments", "disputes",
            "officePickups", "pickupRefunds", "staffApprovals", "waitlistSignups");

        var seller = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(seller, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.FreeId);
        (await PublishAsync(seller, listing.Id)).EnsureSuccessStatusCode();

        var after = await CountsAsync(admin);
        after["adApprovals"].Should().Be(before["adApprovals"] + 1);

        // The bell's "documents to review" link opens the Users list filtered the same way it counts.
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users?status=DocumentsPending&pageSize=100");
        users.GetProperty("totalCount").GetInt32().Should().Be(after["userDocuments"]);
    }

    [Fact]
    public async Task Moderator_OnlySeesTheModerationQueues()
    {
        var (moderator, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.Moderator);

        (await CountsAsync(moderator)).Keys.Should().BeEquivalentTo("reports", "adApprovals", "manualReview");
    }

    [Fact]
    public async Task AppUsers_CannotAsk()
    {
        var seller = await RegisterSellerAsync(_factory);

        (await seller.GetAsync("/api/v1/admin/pending-actions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

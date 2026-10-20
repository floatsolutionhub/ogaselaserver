using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Ogasela.Domain.Accounts;
using Ogasela.Infrastructure.Promotions.Seed;
using Ogasela.IntegrationTests.Admin;
using static Ogasela.IntegrationTests.Listings.ListingsTestHelpers;

namespace Ogasela.IntegrationTests.Listings;

public class AdminUsersTests : IClassFixture<ListingsApiFactory>
{
    private const string SellerPassword = "Sup3rSecret!";
    private readonly ListingsApiFactory _factory;

    public AdminUsersTests(ListingsApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Seller, Guid UserId, string Phone, Guid ListingId)> LiveSellerAsync()
    {
        var seller = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(seller, CategorySeedData.PhonesAndTabletsId);
        var me = await seller.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return (seller, me.GetProperty("id").GetGuid(), me.GetProperty("phone").GetString()!, listing.Id);
    }

    [Fact]
    public async Task SuperAdmin_CanListAndOpenUsers_SellersCannot()
    {
        var (seller, userId, phone, _) = await LiveSellerAsync();
        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);

        var page = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/admin/users?search={phone}");
        var item = page.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetGuid().Should().Be(userId);
        item.GetProperty("liveAdCount").GetInt32().Should().Be(1);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/admin/users/{userId}");
        detail.GetProperty("stats").GetProperty("liveAds").GetInt32().Should().Be(1);
        detail.GetProperty("seller").GetProperty("verificationStatus").GetString().Should().Be("Verified");

        (await seller.GetAsync("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Suspend_BlocksSignInAndPausesAds_ReactivateRestoresSignIn()
    {
        var (_, userId, phone, listingId) = await LiveSellerAsync();
        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);

        (await admin.PostAsJsonAsync($"/api/v1/admin/users/{userId}/suspend", new { reason = "Reported for fraud" }))
            .EnsureSuccessStatusCode();

        var listing = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/v1/listings/{listingId}");
        listing.GetProperty("status").GetString().Should().Be("Paused");

        var blocked = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { phone, password = SellerPassword });
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/admin/users/{userId}");
        detail.GetProperty("isSuspended").GetBoolean().Should().BeTrue();
        detail.GetProperty("suspensionReason").GetString().Should().Be("Reported for fraud");
        detail.GetProperty("stats").GetProperty("activeSessions").GetInt32().Should().Be(0);

        (await admin.PostAsync($"/api/v1/admin/users/{userId}/reactivate", null)).EnsureSuccessStatusCode();
        var allowed = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { phone, password = SellerPassword });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteMyAccount_TakesAdsDown_AndTheAccountCanNoLongerSignIn()
    {
        var (seller, _, phone, listingId) = await LiveSellerAsync();

        (await seller.DeleteAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var listing = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/v1/listings/{listingId}");
        listing.GetProperty("status").GetString().Should().Be("Paused");

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { phone, password = SellerPassword });
        login.IsSuccessStatusCode.Should().BeFalse();
    }

    /// <summary>A buyer chats with the seller about their live ad; returns the buyer and the conversation id.</summary>
    private async Task<(HttpClient Buyer, Guid BuyerId, Guid ConversationId)> ChatAsync(HttpClient seller, Guid listingId)
    {
        var buyer = await RegisterSellerAsync(_factory);
        var buyerId = (await buyer.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("id").GetGuid();
        var start = await buyer.PostAsJsonAsync("/api/v1/conversations", new { listingId });
        start.EnsureSuccessStatusCode();
        var conversationId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await buyer.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/messages", new { content = "Is it still available?" }))
            .EnsureSuccessStatusCode();
        (await seller.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/messages", new { content = "Yes, come by Ikeja." }))
            .EnsureSuccessStatusCode();
        return (buyer, buyerId, conversationId);
    }

    private static async Task ShouldStillSeeTheChatAsync(HttpClient client, Guid conversationId, Guid listingId)
    {
        var conversations = await client.GetFromJsonAsync<JsonElement>("/api/v1/conversations");
        conversations.GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == conversationId)
            .GetProperty("isOtherParticipantDeleted").GetBoolean().Should().BeTrue();
        (await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversationId}"))
            .GetProperty("isOtherParticipantDeleted").GetBoolean().Should().BeTrue();

        var messages = await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversationId}/messages");
        messages.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("content").GetString())
            .Should().Equal("Is it still available?", "Yes, come by Ikeja.");

        // Readable, but closed - nobody is left to read new messages.
        var send = await client.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/messages", new { content = "Hello?" });
        send.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The ad is taken down, not deleted, so the chat can still show what it was about.
        var listing = await client.GetFromJsonAsync<JsonElement>($"/api/v1/listings/{listingId}");
        listing.GetProperty("id").GetGuid().Should().Be(listingId);
    }

    [Fact]
    public async Task DeleteMyAccount_KeepsTheirChats_ForEveryoneTheyDealtWith()
    {
        var (seller, _, _, listingId) = await LiveSellerAsync();
        var (buyer, _, conversationId) = await ChatAsync(seller, listingId);

        (await seller.DeleteAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        await ShouldStillSeeTheChatAsync(buyer, conversationId, listingId);

        // Nobody can open a new chat with the deleted seller either.
        var newBuyer = await RegisterSellerAsync(_factory);
        (await newBuyer.PostAsJsonAsync("/api/v1/conversations", new { listingId })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AdminErasure_KeepsTheUsersChats_ForEveryoneTheyDealtWith()
    {
        var (seller, _, _, listingId) = await LiveSellerAsync();
        var (_, buyerId, conversationId) = await ChatAsync(seller, listingId);
        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);

        (await admin.PostAsync($"/api/v1/admin/data-requests/{buyerId}/erase", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        await ShouldStillSeeTheChatAsync(seller, conversationId, listingId);
    }

    [Fact]
    public async Task DeleteMyAccount_IsRefusedForStaff()
    {
        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);

        (await admin.DeleteAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

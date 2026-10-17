using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Ogasela.Application.Admin.OfficePickups;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Application.OfficePickups;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.OfficePickups;
using Ogasela.Infrastructure.Promotions.Seed;
using Ogasela.IntegrationTests.Admin;
using static Ogasela.IntegrationTests.Listings.ListingsTestHelpers;

namespace Ogasela.IntegrationTests.Payments;

/// <summary>
/// Office pickup end to end: an admin switches it on for a category and opens a branch, a buyer
/// pays item + 3%, staff move the item through inspection, and a refund pays back everything
/// except the 1% cancellation fee.
/// </summary>
public class OfficePickupTests : IClassFixture<PaymentsApiFactory>
{
    // CreateDraftListingAsync prices ads at ₦5,000.
    private const decimal ItemKobo = 500_000m;
    private const decimal FeeKobo = 15_000m;
    private const decimal CancellationFeeKobo = 5_000m;

    private readonly PaymentsApiFactory _factory;

    public OfficePickupTests(PaymentsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OfficePickup_PaidThroughTheWebhook_MovesThroughInspection_AndRefundsLessTheCancellationFee()
    {
        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);
        var seller = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(seller, CategorySeedData.PhonesAndTabletsId);
        var buyer = await RegisterSellerAsync(_factory);

        (await GetDetailAsync(buyer, listing.Id)).OfficePickupAvailable.Should().BeFalse("no category has office pickup yet");

        // Switching on the parent category covers its subcategories.
        (await admin.PutAsJsonAsync($"/api/v1/admin/office-pickups/categories/{CategorySeedData.ElectronicsId}", new { eligible = true }))
            .EnsureSuccessStatusCode();
        var branchResponse = await admin.PostAsJsonAsync("/api/v1/admin/office-pickups/branches", new
        {
            name = "Ikeja", address = "1 Allen Avenue, Ikeja", state = "Lagos", city = "Ikeja",
            latitude = 6.6018m, longitude = 3.3515m, isActive = true
        });
        branchResponse.EnsureSuccessStatusCode();
        var branch = (await branchResponse.Content.ReadFromJsonAsync<AdminOfficeBranch>(JsonOptions))!;

        (await GetDetailAsync(buyer, listing.Id)).OfficePickupAvailable.Should().BeTrue();
        var quote = (await buyer.GetFromJsonAsync<OfficePickupQuote>($"/api/v1/office-pickups/quote/{listing.Id}", JsonOptions))!;
        quote.TotalKobo.Should().Be(ItemKobo + FeeKobo);

        (await seller.PostAsJsonAsync("/api/v1/office-pickups", new { listingId = listing.Id, branchId = branch.Id }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a seller can't office-pickup their own ad");

        var startResponse = await buyer.PostAsJsonAsync("/api/v1/office-pickups", new { listingId = listing.Id, branchId = branch.Id });
        startResponse.EnsureSuccessStatusCode();
        var started = (await startResponse.Content.ReadFromJsonAsync<OfficePickupCheckoutResponse>(JsonOptions))!;
        started.RedirectUrl.Should().NotBeNullOrEmpty();
        (await MineAsync(buyer)).Should().BeEmpty("an unpaid checkout isn't listed");

        (await SendWebhookAsync(started.PaymentReference)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendWebhookAsync(started.PaymentReference)).StatusCode.Should().Be(HttpStatusCode.OK, "a repeat delivery is a no-op");

        var id = started.Request.Id;
        var paid = await GetAsync(buyer, id);
        paid.Summary.Status.Should().Be(OfficePickupStatus.PaymentConfirmed);
        paid.Activities.Should().ContainSingle(a => a.Status == OfficePickupStatus.PaymentConfirmed);
        (await MineAsync(seller)).Should().ContainSingle(s => s.Id == id && s.MyRole == "Seller");
        (await InboxTypesAsync(seller)).Should().Contain("OfficePickupUpdate");

        (await UpdateStatusAsync(admin, id, OfficePickupStatus.Completed)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await UpdateStatusAsync(admin, id, OfficePickupStatus.ItemReceived)).EnsureSuccessStatusCode();
        (await UpdateStatusAsync(admin, id, OfficePickupStatus.InspectionFailed)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "a failed inspection needs a reason");
        (await UpdateStatusAsync(admin, id, OfficePickupStatus.InspectionFailed, "Screen is cracked")).EnsureSuccessStatusCode();

        (await buyer.PostAsJsonAsync($"/api/v1/office-pickups/{id}/cancel", new { reason = "Item failed inspection" }))
            .EnsureSuccessStatusCode();
        var adminView = (await admin.GetFromJsonAsync<AdminOfficePickupDetail>($"/api/v1/admin/office-pickups/{id}", JsonOptions))!;
        adminView.CancellationRequestedAt.Should().NotBeNull();
        adminView.RefundPreviewKobo.Should().Be(ItemKobo + FeeKobo - CancellationFeeKobo);

        (await admin.PostAsJsonAsync($"/api/v1/admin/office-pickups/{id}/refund", new { reason = "Failed inspection" }))
            .EnsureSuccessStatusCode();
        _factory.Gateway.PartialRefunds[started.PaymentReference].Should().Be(ItemKobo + FeeKobo - CancellationFeeKobo);

        var refunded = await GetAsync(buyer, id);
        refunded.Summary.Status.Should().Be(OfficePickupStatus.Refunded);
        refunded.RefundedKobo.Should().Be(ItemKobo + FeeKobo - CancellationFeeKobo);
        refunded.CancellationFeeKobo.Should().Be(CancellationFeeKobo);

        (await admin.PostAsJsonAsync($"/api/v1/admin/office-pickups/{id}/refund", new { reason = "Again" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "a request is only ever refunded once");
    }

    [Fact]
    public async Task ClosingThePaymentPageWithoutPaying_CancelsThePickup_AndHidesIt()
    {
        var (admin, _) = await AdminTestHelpers.RegisterInternalUserAsync(_factory, UserRole.SuperAdmin);
        var seller = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(seller, CategorySeedData.PhonesAndTabletsId);
        var buyer = await RegisterSellerAsync(_factory);
        (await admin.PutAsJsonAsync($"/api/v1/admin/office-pickups/categories/{CategorySeedData.ElectronicsId}", new { eligible = true }))
            .EnsureSuccessStatusCode();
        var branch = (await (await admin.PostAsJsonAsync("/api/v1/admin/office-pickups/branches", new
        {
            name = "Lekki", address = "2 Admiralty Way", state = "Lagos", latitude = 6.4474m, longitude = 3.4730m, isActive = true
        })).Content.ReadFromJsonAsync<AdminOfficeBranch>(JsonOptions))!;

        var started = (await (await buyer.PostAsJsonAsync("/api/v1/office-pickups", new { listingId = listing.Id, branchId = branch.Id }))
            .Content.ReadFromJsonAsync<OfficePickupCheckoutResponse>(JsonOptions))!;
        _factory.Gateway.MarkAbandoned(started.PaymentReference);

        var response = await buyer.PostAsync($"/api/v1/office-pickups/{started.Request.Id}/cancel-payment", null);
        response.EnsureSuccessStatusCode();
        var cancelled = (await response.Content.ReadFromJsonAsync<OfficePickupDetail>(JsonOptions))!;

        cancelled.Summary.Status.Should().Be(OfficePickupStatus.PaymentCancelled);
        (await MineAsync(buyer)).Should().BeEmpty();
        (await MineAsync(seller)).Should().BeEmpty("the seller is never told about an unpaid request");
    }

    private static async Task<ListingDetailResponse> GetDetailAsync(HttpClient client, Guid listingId) =>
        (await client.GetFromJsonAsync<ListingDetailResponse>($"/api/v1/listings/{listingId}", JsonOptions))!;

    private static async Task<IReadOnlyList<OfficePickupSummary>> MineAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<OfficePickupSummary>>("/api/v1/office-pickups/mine", JsonOptions))!;

    private static async Task<OfficePickupDetail> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<OfficePickupDetail>($"/api/v1/office-pickups/{id}", JsonOptions))!;

    private static Task<HttpResponseMessage> UpdateStatusAsync(HttpClient admin, Guid id, OfficePickupStatus status, string? note = null) =>
        admin.PostAsJsonAsync($"/api/v1/admin/office-pickups/{id}/status", new { status = status.ToString(), note }, JsonOptions);

    private static async Task<IEnumerable<string>> InboxTypesAsync(HttpClient client)
    {
        var inbox = await client.GetFromJsonAsync<JsonElement>("/api/v1/me/inbox?pageSize=50");
        return inbox.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()!).ToList();
    }

    private async Task<HttpResponseMessage> SendWebhookAsync(string reference)
    {
        var body = FakePaymentGateway.BuildWebhookBody(reference);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/paystack")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-paystack-signature", FakePaymentGateway.ComputeSignature(body));
        return await _factory.CreateClient().SendAsync(request);
    }
}

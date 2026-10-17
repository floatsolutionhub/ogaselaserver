using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ogasela.Api.Contracts.Listings;
using Ogasela.Api.Contracts.Payments;
using Ogasela.Application.Listings.Checkout;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Application.Payments.FundWallet;
using Ogasela.Application.Payments.GetWallet;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Payments;
using Ogasela.Infrastructure.Persistence;
using Ogasela.Infrastructure.Promotions.Seed;
using static Ogasela.IntegrationTests.Listings.ListingsTestHelpers;

namespace Ogasela.IntegrationTests.Payments;

/// <summary>
/// The ad checkout: choose a plan, pay, and only then is the ad submitted for review. Runs with
/// moderator approval on (the production default), so a settled checkout lands in PendingReview.
/// </summary>
public class ListingCheckoutTests : IClassFixture<PaymentsApiFactory>
{
    private const decimal BasicPlanPriceKobo = 75_000m;
    private readonly PaymentsApiFactory _gatewayHost;
    private readonly WebApplicationFactory<Program> _factory;

    public ListingCheckoutTests(PaymentsApiFactory factory)
    {
        _gatewayHost = factory;
        _factory = factory.WithWebHostBuilder(b => b.UseSetting("Listings:RequireApproval", "true"));
    }

    [Fact]
    public async Task CardCheckout_StaysDraftUntilPaid_ThenConfirmSubmitsItForReview_AndRepeatConfirmIsANoOp()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(client, CategorySeedData.PhonesAndTabletsId, null);

        var started = await CheckoutAsync(client, listing.Id, PromotionPlanSeedData.BasicId, ListingPaymentMethod.Card);
        started.Outcome.Should().Be(ListingCheckoutOutcome.PaymentRequired);
        started.RedirectUrl.Should().NotBeNullOrEmpty();
        (await GetListingAsync(client, listing.Id)).Status.Should().Be(ListingStatus.Draft, "nothing is paid yet");

        var confirmed = await ConfirmAsync(client, listing.Id, started.PaymentReference!);
        confirmed.Outcome.Should().Be(ListingCheckoutOutcome.Submitted);
        confirmed.Listing.Status.Should().Be(ListingStatus.PendingReview);

        var again = await ConfirmAsync(client, listing.Id, started.PaymentReference!);
        again.Outcome.Should().Be(ListingCheckoutOutcome.Submitted);

        var charges = await PlanChargesAsync(listing.Id);
        charges.Should().ContainSingle(t => t.Status == TransactionStatus.Success && t.AmountKobo == BasicPlanPriceKobo);
    }

    [Fact]
    public async Task CardCheckout_ConfirmedByTheWebhook_SubmitsTheListing()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(client, CategorySeedData.PhonesAndTabletsId, null);
        var started = await CheckoutAsync(client, listing.Id, PromotionPlanSeedData.BasicId, ListingPaymentMethod.Card);

        var webhook = await SendWebhookAsync(started.PaymentReference!);
        webhook.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetListingAsync(client, listing.Id)).Status.Should().Be(ListingStatus.PendingReview);
        (await GetWalletAsync(client)).BalanceKobo.Should().Be(0m, "an ad payment pays for the ad, it doesn't top up the wallet");
    }

    [Fact]
    public async Task CardCheckout_ThatFailsAtTheGateway_LeavesTheListingADraft()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(client, CategorySeedData.PhonesAndTabletsId, null);
        var started = await CheckoutAsync(client, listing.Id, PromotionPlanSeedData.BasicId, ListingPaymentMethod.Card);
        _gatewayHost.Gateway.MarkVerificationAsFailed(started.PaymentReference!);

        var confirmed = await ConfirmAsync(client, listing.Id, started.PaymentReference!);

        confirmed.Outcome.Should().Be(ListingCheckoutOutcome.PaymentFailed);
        confirmed.Listing.Status.Should().Be(ListingStatus.Draft);
    }

    [Fact]
    public async Task WalletCheckout_DebitsAndSubmits_OrFailsCleanlyWhenTheBalanceIsShort()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(client, CategorySeedData.PhonesAndTabletsId, null);

        var short_ = await client.PostAsJsonAsync($"/api/v1/listings/{listing.Id}/checkout",
            new ListingCheckoutRequest(PromotionPlanSeedData.BasicId, ListingPaymentMethod.Wallet));
        short_.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        (await GetListingAsync(client, listing.Id)).Status.Should().Be(ListingStatus.Draft);

        await FundAndConfirmAsync(client, BasicPlanPriceKobo);
        var paid = await CheckoutAsync(client, listing.Id, PromotionPlanSeedData.BasicId, ListingPaymentMethod.Wallet);

        paid.Outcome.Should().Be(ListingCheckoutOutcome.Submitted);
        paid.Listing.Status.Should().Be(ListingStatus.PendingReview);
        (await GetWalletAsync(client)).BalanceKobo.Should().Be(0m);
    }

    [Fact]
    public async Task Checkout_OfAnIncompleteDraft_IsRefusedBeforeAnyPaymentStarts()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(client, CategorySeedData.PhonesAndTabletsId, null, mediaUrls: []);

        var response = await client.PostAsJsonAsync($"/api/v1/listings/{listing.Id}/checkout",
            new ListingCheckoutRequest(PromotionPlanSeedData.BasicId, ListingPaymentMethod.Card));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PlanChargesAsync(listing.Id)).Should().BeEmpty("no payment may start for an ad that can't be submitted");
    }

    [Fact]
    public async Task ACardPaymentConfirmedAfterTheAdWasAlreadyPaidFor_IsCreditedToTheWallet()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(client, CategorySeedData.PhonesAndTabletsId, null);

        // Seller starts a card payment, abandons the page, then pays from the wallet instead...
        var card = await CheckoutAsync(client, listing.Id, PromotionPlanSeedData.BasicId, ListingPaymentMethod.Card);
        await FundAndConfirmAsync(client, BasicPlanPriceKobo);
        await CheckoutAsync(client, listing.Id, PromotionPlanSeedData.BasicId, ListingPaymentMethod.Wallet);

        // ...and the card payment turns out to have gone through after all.
        var late = await ConfirmAsync(client, listing.Id, card.PaymentReference!);

        late.Listing.Status.Should().Be(ListingStatus.PendingReview);
        (await GetWalletAsync(client)).BalanceKobo.Should().Be(BasicPlanPriceKobo, "a double payment must never be lost");
    }

    [Fact]
    public async Task ClosingThePaymentPageWithoutPaying_RecordsACancelledPayment_ButALateSuccessStillSubmits()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(client, CategorySeedData.PhonesAndTabletsId, null);
        var started = await CheckoutAsync(client, listing.Id, PromotionPlanSeedData.BasicId, ListingPaymentMethod.Card);
        _gatewayHost.Gateway.MarkAbandoned(started.PaymentReference!);

        var response = await client.PostAsync($"/api/v1/listings/{listing.Id}/checkout/{started.PaymentReference}/cancel", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = (await response.Content.ReadFromJsonAsync<ListingCheckoutResponse>(JsonOptions))!;

        cancelled.Outcome.Should().Be(ListingCheckoutOutcome.PaymentCancelled);
        cancelled.Listing.Status.Should().Be(ListingStatus.Draft);
        (await PlanChargesAsync(listing.Id)).Should().ContainSingle(t => t.Status == TransactionStatus.Cancelled);

        // The bank confirms after all (e.g. a transfer that landed late): the money must not be lost.
        _gatewayHost.Gateway.MarkSucceeded(started.PaymentReference!);
        (await SendWebhookAsync(started.PaymentReference!)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetListingAsync(client, listing.Id)).Status.Should().Be(ListingStatus.PendingReview);
    }

    [Fact]
    public async Task ClosingThePaymentPageAfterPaying_SettlesInsteadOfCancelling()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateDraftListingAsync(client, CategorySeedData.PhonesAndTabletsId, null);
        var started = await CheckoutAsync(client, listing.Id, PromotionPlanSeedData.BasicId, ListingPaymentMethod.Card);

        var response = await client.PostAsync($"/api/v1/listings/{listing.Id}/checkout/{started.PaymentReference}/cancel", null);
        var result = (await response.Content.ReadFromJsonAsync<ListingCheckoutResponse>(JsonOptions))!;

        result.Outcome.Should().Be(ListingCheckoutOutcome.Submitted);
    }

    private static async Task<ListingCheckoutResponse> CheckoutAsync(
        HttpClient client, Guid listingId, Guid planId, ListingPaymentMethod method)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/listings/{listingId}/checkout", new ListingCheckoutRequest(planId, method));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ListingCheckoutResponse>(JsonOptions))!;
    }

    private static async Task<ListingCheckoutResponse> ConfirmAsync(HttpClient client, Guid listingId, string reference)
    {
        var response = await client.PostAsync($"/api/v1/listings/{listingId}/checkout/{reference}/confirm", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ListingCheckoutResponse>(JsonOptions))!;
    }

    private static async Task<ListingResponse> GetListingAsync(HttpClient client, Guid listingId) =>
        (await (await client.GetAsync($"/api/v1/listings/{listingId}")).Content.ReadFromJsonAsync<ListingResponse>(JsonOptions))!;

    private static async Task<WalletResponse> GetWalletAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/wallet")).Content.ReadFromJsonAsync<WalletResponse>(JsonOptions))!;

    private async Task<List<Transaction>> PlanChargesAsync(Guid listingId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OgaselaDbContext>();
        return await dbContext.Transactions
            .Where(t => t.RelatedListingId == listingId && t.Type == TransactionType.PlanPurchase)
            .ToListAsync();
    }

    private async Task FundAndConfirmAsync(HttpClient client, decimal amountKobo)
    {
        var fundResponse = await client.PostAsJsonAsync("/api/v1/wallet/fund", new FundWalletRequest(amountKobo));
        var funded = await fundResponse.Content.ReadFromJsonAsync<FundWalletResponse>(JsonOptions);
        (await SendWebhookAsync(funded!.GatewayReference)).StatusCode.Should().Be(HttpStatusCode.OK);
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

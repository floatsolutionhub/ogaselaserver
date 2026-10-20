using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Ogasela.Api.Contracts.Listings;
using Ogasela.Application.Accounts.GetCurrentUser;
using Ogasela.Application.Listings.GetListing;
using Ogasela.Application.Listings.GetSellerListings;
using Ogasela.Application.Search;
using Ogasela.Domain.Listings;
using Ogasela.Infrastructure.Promotions.Seed;
using static Ogasela.IntegrationTests.Listings.ListingsTestHelpers;

namespace Ogasela.IntegrationTests.Search;

public class SearchListingsTests : IClassFixture<SearchApiFactory>
{
    private readonly SearchApiFactory _factory;

    public SearchListingsTests(SearchApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Search_WithARelevanceTiedQuery_RanksThePremiumListingAboveAnOtherwiseIdenticalFreeListing()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);

        const string token = "Zephyr9000";
        const string title = $"{token} Vintage Camera";
        const string description = "A rare vintage camera in excellent working condition, fully tested and serviced.";

        var freeListing = await CreateAndPublishAsync(
            client, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.FreeId, title, description);
        var premiumListing = await CreateAndPublishAsync(
            client, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.PremiumId, title, description);

        var page = await SearchAsync(new Dictionary<string, string?> { ["query"] = token });

        var ids = page.Items.Select(i => i.Id).ToList();
        ids.Should().Contain(freeListing.Id).And.Contain(premiumListing.Id);

        var premiumIndex = ids.IndexOf(premiumListing.Id);
        var freeIndex = ids.IndexOf(freeListing.Id);
        premiumIndex.Should().BeLessThan(
            freeIndex, "a Premium listing must outrank an otherwise text-identical Free listing via its BoostWeight");

        var premiumItem = page.Items.Single(i => i.Id == premiumListing.Id);
        var freeItem = page.Items.Single(i => i.Id == freeListing.Id);
        premiumItem.Score.Should().BeGreaterThan(freeItem.Score);
        premiumItem.PlanTier.Should().Be(Ogasela.Domain.Promotions.PromotionPlanName.Premium);
        freeItem.PlanTier.Should().Be(Ogasela.Domain.Promotions.PromotionPlanName.Free);
    }

    [Fact]
    public async Task Search_WithCategoryAndPriceRangeFilters_NarrowsToOnlyMatchingListings()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        const string token = "FilterMarkerQ7";

        var cheapElectronics = await CreateAndPublishAsync(
            client, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.BasicId,
            $"{token} Cheap Phone", "An affordable phone.", price: 10_000m);
        var midElectronics = await CreateAndPublishAsync(
            client, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.BasicId,
            $"{token} Mid Range Phone", "A mid-range phone.", price: 50_000m);
        var expensiveElectronics = await CreateAndPublishAsync(
            client, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.BasicId,
            $"{token} Flagship Phone", "A flagship phone.", price: 500_000m);
        var outOfCategory = await CreateAndPublishAsync(
            client, CategorySeedData.WomensClothingId, PromotionPlanSeedData.BasicId,
            $"{token} Designer Jacket", "A designer jacket.", price: 50_000m);

        var page = await SearchAsync(new Dictionary<string, string?>
        {
            ["query"] = token,
            ["categoryId"] = CategorySeedData.ElectronicsId.ToString(),
            ["minPrice"] = "20000",
            ["maxPrice"] = "100000"
        });

        var ids = page.Items.Select(i => i.Id).ToList();
        ids.Should().ContainSingle().Which.Should().Be(midElectronics.Id);
        ids.Should().NotContain(cheapElectronics.Id);
        ids.Should().NotContain(expensiveElectronics.Id);
        ids.Should().NotContain(outOfCategory.Id);
    }

    [Fact]
    public async Task Search_WithAPartiallyTypedWord_MatchesByPrefix()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishAsync(
            client, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.BasicId,
            "Quixotronic Handset", "A brand new handset in its box.");

        var page = await SearchAsync(new Dictionary<string, string?> { ["query"] = "quixotro" });

        page.Items.Select(i => i.Id).Should().Contain(listing.Id);
    }

    [Fact]
    public async Task Search_ByParentCategoryName_FindsListingsInItsSubcategories()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishAsync(
            client, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.BasicId,
            "Vexillary Handset", "Barely used.");

        var page = await SearchAsync(new Dictionary<string, string?> { ["query"] = "vexillary electronics" });

        page.Items.Select(i => i.Id).Should().Contain(listing.Id);
    }

    [Fact]
    public async Task Search_AcrossMultiplePages_ReturnsStableNonDuplicatedResults()
    {
        var client = await RegisterAndVerifySellerAsync(_factory);
        const string token = "PagerMarkerV3";
        const int totalListings = 25;
        const int pageSize = 10;

        var createdIds = new List<Guid>();
        for (var i = 1; i <= totalListings; i++)
        {
            var listing = await CreateAndPublishAsync(
                client, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.BasicId,
                $"{token} Item {i:D2}", "A paginated test listing.");
            createdIds.Add(listing.Id);
        }

        var seenIds = new List<Guid>();
        int? totalCount = null;

        for (var page = 1; page <= 3; page++)
        {
            var result = await SearchAsync(new Dictionary<string, string?>
            {
                ["query"] = token,
                ["sort"] = "newest",
                ["page"] = page.ToString(),
                ["pageSize"] = pageSize.ToString()
            });

            totalCount ??= result.TotalCount;
            result.TotalCount.Should().Be(totalCount, "the total count must stay stable across pages");

            seenIds.AddRange(result.Items.Select(i => i.Id));
        }

        seenIds.Should().HaveCount(totalListings, "every created listing should appear exactly once across the three pages");
        seenIds.Distinct().Should().HaveCount(totalListings, "no listing should be duplicated across pages");
        seenIds.Should().BeEquivalentTo(createdIds);
    }

    [Fact]
    public async Task Search_HidesAdsFromSellersTheViewerBlocked_AndTheirStorefrontComesBackEmpty()
    {
        var sellerClient = await RegisterAndVerifySellerAsync(_factory);
        const string token = "BlockMarkerK4";
        var listing = await CreateAndPublishAsync(
            sellerClient, CategorySeedData.PhonesAndTabletsId, PromotionPlanSeedData.FreeId,
            $"{token} Used Phone", "A used phone in good condition.");
        var sellerMe = await sellerClient.GetFromJsonAsync<CurrentUserResponse>("/api/v1/me", JsonOptions);

        var viewerClient = await RegisterSellerAsync(_factory);
        (await viewerClient.PostAsync($"/api/v1/users/{sellerMe!.Id}/block", content: null)).EnsureSuccessStatusCode();

        var viewerPage = await viewerClient.GetFromJsonAsync<SearchResultPage>($"/api/v1/search?query={token}", JsonOptions);
        viewerPage!.Items.Should().NotContain(i => i.Id == listing.Id);

        var storefront = await viewerClient.GetFromJsonAsync<PagedListingsResponse>(
            $"/api/v1/sellers/{listing.SellerId}/listings", JsonOptions);
        storefront!.Items.Should().BeEmpty();

        var detail = await viewerClient.GetFromJsonAsync<ListingDetailResponse>($"/api/v1/listings/{listing.Id}", JsonOptions);
        detail!.Seller.IsBlockedByMe.Should().BeTrue();

        // Everyone else still sees it.
        var anonymousPage = await SearchAsync(new Dictionary<string, string?> { ["query"] = token });
        anonymousPage.Items.Should().Contain(i => i.Id == listing.Id);
    }

    private async Task<ListingResponse> CreateAndPublishAsync(
        HttpClient client, Guid categoryId, Guid promotionPlanId, string title, string description, decimal? price = null)
    {
        var request = new CreateListingRequest(
            title, description, categoryId, price, ListingCondition.Used,
            ["https://example.com/photo.jpg"], promotionPlanId);

        var createResponse = await client.PostAsJsonAsync("/api/v1/listings", request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listing = await createResponse.Content.ReadFromJsonAsync<ListingResponse>(JsonOptions);

        var publishResponse = await client.PostAsync($"/api/v1/listings/{listing!.Id}/publish", null);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await publishResponse.Content.ReadFromJsonAsync<ListingResponse>(JsonOptions))!;
    }

    private async Task<SearchResultPage> SearchAsync(Dictionary<string, string?> queryParams)
    {
        var client = _factory.CreateClient();
        var url = QueryHelpers.AddQueryString("/api/v1/search", queryParams);

        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<SearchResultPage>(JsonOptions))!;
    }
}

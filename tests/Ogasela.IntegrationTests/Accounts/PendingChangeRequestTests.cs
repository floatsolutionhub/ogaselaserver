using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Ogasela.Application.Accounts.ProfileChanges;
using Ogasela.IntegrationTests.Listings;
using static Ogasela.IntegrationTests.Listings.ListingsTestHelpers;

namespace Ogasela.IntegrationTests.Accounts;

/// <summary>Only one change of each kind may wait for review at a time - a new one is refused, not swapped in.</summary>
public class PendingChangeRequestTests : IClassFixture<ListingsApiFactory>
{
    private readonly ListingsApiFactory _factory;

    public PendingChangeRequestTests(ListingsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ABusinessInfoChange_CannotBeResubmittedWhilePending_ButCanAfterWithdrawing()
    {
        var seller = await RegisterSellerAsync(_factory);

        var first = await seller.PostAsJsonAsync("/api/v1/me/change-requests/business", new { businessName = "Ada Fabrics Ltd", storeAddress = "1 Broad St" });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var pending = (await first.Content.ReadFromJsonAsync<ProfileChangeResponse>(JsonOptions))!;

        var second = await seller.PostAsJsonAsync("/api/v1/me/change-requests/business", new { businessName = "Ada Couture", storeAddress = "2 Broad St" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain("ProfileChange.AlreadyPending");

        // The first request is still the one under review - it wasn't replaced.
        var mine = (await seller.GetFromJsonAsync<List<ProfileChangeResponse>>("/api/v1/me/change-requests", JsonOptions))!;
        mine.Should().ContainSingle(r => r.Status == Domain.Accounts.ProfileChangeStatus.PendingReview && r.Id == pending.Id);

        (await seller.DeleteAsync($"/api/v1/me/change-requests/{pending.Id}")).EnsureSuccessStatusCode();
        (await seller.PostAsJsonAsync("/api/v1/me/change-requests/business", new { businessName = "Ada Couture", storeAddress = "2 Broad St" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

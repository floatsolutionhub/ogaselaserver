using FluentAssertions;
using Ogasela.Domain.Listings;

namespace Ogasela.UnitTests.Listings;

/// <summary>My Ads lifecycle: edits of live ads, renewals, plan upgrades and deleting.</summary>
public class ListingLifecycleTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Listing Draft() => Listing.CreateDraft(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Laptop", "Barely used laptop",
        250_000m, ListingCondition.Used, ["https://cdn.example/a.jpg"], Now);

    /// <summary>Submitted, then approved with a 30-day plan at <see cref="Now"/>.</summary>
    private static Listing Live()
    {
        var listing = Draft();
        listing.SubmitForReview(Now);
        listing.ApproveReview(Now, 30);
        return listing;
    }

    [Fact]
    public void ApprovingAFreshSubmission_StartsThePlanPeriodNow()
    {
        var listing = Draft();
        listing.SubmitForReview(Now);
        listing.IsFreshSubmission.Should().BeTrue();

        listing.ApproveReview(Now.AddDays(2), 30);

        listing.Status.Should().Be(ListingStatus.Active);
        listing.ExpiresAt.Should().Be(Now.AddDays(32));
    }

    [Fact]
    public void EditingALiveAd_GoesToReviewAndKeepsItsExpiry()
    {
        var listing = Live();

        listing.SubmitEditForReview(Now.AddDays(5));

        listing.Status.Should().Be(ListingStatus.PendingReview);
        listing.IsFreshSubmission.Should().BeFalse();
        listing.ExpiresAt.Should().Be(Now.AddDays(30));
    }

    [Fact]
    public void ApprovingAnEdit_GoesBackLiveOnTheSamePeriod()
    {
        var listing = Live();
        listing.SubmitEditForReview(Now.AddDays(5));

        listing.ApproveReview(Now.AddDays(6), 30);

        listing.Status.Should().Be(ListingStatus.Active);
        listing.ExpiresAt.Should().Be(Now.AddDays(30));
    }

    [Fact]
    public void ApprovingAnEditAfterThePeriodRanOut_LeavesItExpired()
    {
        var listing = Live();
        listing.SubmitEditForReview(Now.AddDays(29));

        listing.ApproveReview(Now.AddDays(31), 30);

        listing.Status.Should().Be(ListingStatus.Expired);
    }

    [Fact]
    public void ARejectedEdit_IsADraftWhosePlanKeepsRunning()
    {
        var listing = Live();
        listing.SubmitEditForReview(Now.AddDays(5));

        listing.RejectReview("Blurry photos", Now.AddDays(6));

        listing.Status.Should().Be(ListingStatus.Draft);
        listing.HasRunningPlanPeriod(Now.AddDays(6)).Should().BeTrue();
        listing.ExpiresAt.Should().Be(Now.AddDays(30));
    }

    [Fact]
    public void ANewDraft_HasNoRunningPlanPeriod()
    {
        Draft().HasRunningPlanPeriod(Now).Should().BeFalse();
    }

    [Fact]
    public void RenewingAnExpiredAd_DropsTheOldExpiry()
    {
        var listing = Live();
        listing.ApproveReview(Now.AddDays(31), 30); // edit-free: still on its period, which ran out
        listing.SubmitForReview(Now.AddDays(40));

        listing.IsFreshSubmission.Should().BeTrue();
        listing.ApproveReview(Now.AddDays(41), 14);

        listing.Status.Should().Be(ListingStatus.Active);
        listing.ExpiresAt.Should().Be(Now.AddDays(55));
    }

    [Fact]
    public void ChangingThePlanOfALiveAd_StartsTheNewPeriodImmediately()
    {
        var listing = Live();
        var premium = Guid.NewGuid();

        listing.ChangePlanNow(premium, 60, Now.AddDays(20));

        listing.PromotionPlanId.Should().Be(premium);
        listing.ExpiresAt.Should().Be(Now.AddDays(80));
        listing.Status.Should().Be(ListingStatus.Active);
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.PendingReview)]
    [InlineData(ListingStatus.Expired)]
    public void DraftPendingAndExpiredAds_CanBeDeleted(ListingStatus status)
    {
        var listing = Draft();
        if (status == ListingStatus.PendingReview)
        {
            listing.SubmitForReview(Now);
        }
        else if (status == ListingStatus.Expired)
        {
            listing.Publish(Now, Now.AddDays(1));
            listing.ApproveReview(Now.AddDays(2), 1);
        }

        listing.Status.Should().Be(status);
        listing.Delete(Now.AddDays(3));

        listing.Status.Should().Be(ListingStatus.Deleted);
    }

    [Fact]
    public void ALiveAd_CannotBeDeleted()
    {
        var listing = Live();

        listing.CanBeDeleted.Should().BeFalse();
        listing.Invoking(l => l.Delete(Now)).Should().Throw<InvalidOperationException>();
    }
}

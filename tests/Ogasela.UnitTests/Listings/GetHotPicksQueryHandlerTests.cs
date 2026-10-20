using FluentAssertions;
using Ogasela.Application.Analytics;
using Ogasela.Application.Listings.GetHotPicks;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.Analytics;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Messaging;
using Ogasela.Domain.Promotions;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Listings;

public class GetHotPicksQueryHandlerTests
{
    private sealed class NoOpTracker : IEngagementTracker
    {
        public Task RecordAsync(IReadOnlyCollection<(Guid ListingId, Guid SellerId)> listings, ListingEngagement engagement, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordProfileVisitAsync(Guid sellerId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static readonly Guid Phones = Guid.NewGuid();
    private static readonly Guid Electronics = Guid.NewGuid();
    private static readonly Guid Cars = Guid.NewGuid();
    private static readonly Guid Land = Guid.NewGuid();

    private static (GetHotPicksQueryHandler Handler, TestApplicationDbContext Db, FakeDateTime Clock, PromotionPlan Plan) CreateSut(
        Guid? viewerId = null)
    {
        var db = TestApplicationDbContext.Create();
        var clock = new FakeDateTime();
        db.Categories.AddRange(
            Category.Create(Electronics, "Electronics", null, true, 1, null, clock.UtcNow),
            Category.Create(Phones, "Phones", Electronics, true, 1, null, clock.UtcNow),
            Category.Create(Cars, "Cars", null, true, 1, null, clock.UtcNow),
            Category.Create(Land, "Land", null, true, 1, null, clock.UtcNow));
        var plan = PromotionPlan.Create(Guid.NewGuid(), PromotionPlanName.Free, 7, 3, false, 0, 0m, AiToolTier.Basic, false, 0, true);
        db.PromotionPlans.Add(plan);
        db.SaveChanges();
        return (new GetHotPicksQueryHandler(db, clock, new NoOpTracker(), new FakeCurrentUserService { UserId = viewerId }), db, clock, plan);
    }

    private static Listing AddLive(
        TestApplicationDbContext db, FakeDateTime clock, PromotionPlan plan, Guid categoryId, string title, string? location = null,
        Guid? sellerProfileId = null)
    {
        var listing = Listing.CreateDraft(Guid.NewGuid(), sellerProfileId ?? Guid.NewGuid(), categoryId, plan.Id, title, "A good item for sale",
            1000m, ListingCondition.Used, ["https://example.com/a.jpg"], clock.UtcNow.AddDays(-3));
        if (location is not null) listing.SetLocation(location, null, null, clock.UtcNow);
        listing.Publish(clock.UtcNow.AddDays(-3), clock.UtcNow.AddDays(4));
        db.Listings.Add(listing);
        return listing;
    }

    [Fact]
    public async Task Handle_LeavesOutAdsFromSellersTheViewerBlocked()
    {
        var viewerId = Guid.NewGuid();
        var (handler, db, clock, plan) = CreateSut(viewerId);
        var blockedSeller = SellerProfile.Create(Guid.NewGuid(), "Blocked Shop", null, null);
        db.SellerProfiles.Add(blockedSeller);
        db.BlockedUsers.Add(BlockedUser.Create(viewerId, blockedSeller.UserId, clock.UtcNow));
        AddLive(db, clock, plan, Phones, "Blocked phone", sellerProfileId: blockedSeller.Id);
        AddLive(db, clock, plan, Phones, "Other phone");
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await handler.Handle(new GetHotPicksQuery(null, 1, 50, Seed: 1), CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.Title.Should().Be("Other phone");
    }

    [Fact]
    public async Task Handle_CapsAtFiftyAndMixesCategories()
    {
        var (handler, db, clock, plan) = CreateSut();
        for (var i = 0; i < 60; i++) AddLive(db, clock, plan, Phones, $"Phone {i}");
        for (var i = 0; i < 5; i++) AddLive(db, clock, plan, Cars, $"Car {i}");
        for (var i = 0; i < 5; i++) AddLive(db, clock, plan, Land, $"Land {i}");
        await db.SaveChangesAsync(CancellationToken.None);

        var all = await handler.Handle(new GetHotPicksQuery(null, 1, 50, Seed: 7), CancellationToken.None);

        all.Value.TotalCount.Should().Be(50);
        all.Value.Items.Count(i => i.CategoryId == Cars).Should().Be(5);
        all.Value.Items.Count(i => i.CategoryId == Land).Should().Be(5);
        // Every category gets a slot among the first few, so one busy category can't crowd the top.
        all.Value.Items.Take(10).Select(i => i.CategoryId).Should().Contain([Phones, Cars, Land]);
    }

    [Fact]
    public async Task Handle_HighlyEngagedAdLandsInTheFirstBlock()
    {
        var (handler, db, clock, plan) = CreateSut();
        Listing? hot = null;
        for (var i = 0; i < 30; i++)
        {
            var listing = AddLive(db, clock, plan, Phones, $"Phone {i}");
            if (i == 25) hot = listing;
        }
        db.ListingDailyStats.Add(ListingDailyStat.Create(hot!.Id, hot.SellerId, DateOnly.FromDateTime(clock.UtcNow), 500, 120, 30, 20, 15));
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await handler.Handle(new GetHotPicksQuery(null, 1, 10, Seed: 3), CancellationToken.None);

        result.Value.Items.Select(i => i.Id).Should().Contain(hot.Id);
    }

    [Fact]
    public async Task Handle_SameSeedGivesStablePagesWithoutDuplicates()
    {
        var (handler, db, clock, plan) = CreateSut();
        for (var i = 0; i < 25; i++) AddLive(db, clock, plan, i % 2 == 0 ? Phones : Cars, $"Item {i}");
        await db.SaveChangesAsync(CancellationToken.None);

        var page1 = await handler.Handle(new GetHotPicksQuery(null, 1, 10, Seed: 42), CancellationToken.None);
        var page1Again = await handler.Handle(new GetHotPicksQuery(null, 1, 10, Seed: 42), CancellationToken.None);
        var page2 = await handler.Handle(new GetHotPicksQuery(null, 2, 10, Seed: 42), CancellationToken.None);

        page1Again.Value.Items.Select(i => i.Id).Should().Equal(page1.Value.Items.Select(i => i.Id));
        page2.Value.Items.Select(i => i.Id).Should().NotIntersectWith(page1.Value.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Handle_CategoryFilter_IncludesAdsInTheCategoryAndItsSubcategories()
    {
        var (handler, db, clock, plan) = CreateSut();
        var direct = AddLive(db, clock, plan, Electronics, "Posted straight to Electronics");
        var inSub = AddLive(db, clock, plan, Phones, "iPhone");
        AddLive(db, clock, plan, Cars, "Camry");
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await handler.Handle(new GetHotPicksQuery(Electronics, 1, 50, Seed: 1), CancellationToken.None);

        result.Value.Items.Select(i => i.Id).Should().BeEquivalentTo([direct.Id, inSub.Id]);
    }

    [Fact]
    public async Task Handle_LocationFilter_KeepsOnlyAdsFromThatPlace_CaseInsensitive()
    {
        var (handler, db, clock, plan) = CreateSut();
        var ikeja = AddLive(db, clock, plan, Phones, "Phone in Ikeja", "Ikeja, Lagos");
        AddLive(db, clock, plan, Phones, "Phone in Wuse", "Wuse 2, Abuja");
        AddLive(db, clock, plan, Cars, "Car with no location");
        await db.SaveChangesAsync(CancellationToken.None);

        var inIkeja = await handler.Handle(new GetHotPicksQuery(null, 1, 50, 1, "ikeja"), CancellationToken.None);
        var inOhafia = await handler.Handle(new GetHotPicksQuery(null, 1, 50, 1, "Ohafia"), CancellationToken.None);

        inIkeja.Value.Items.Select(i => i.Id).Should().Equal(ikeja.Id);
        inOhafia.Value.TotalCount.Should().Be(0);
    }
}

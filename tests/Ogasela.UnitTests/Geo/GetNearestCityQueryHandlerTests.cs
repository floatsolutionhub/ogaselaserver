using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Geo;
using Ogasela.Application.Geo.GetNearestCity;
using Ogasela.Domain.Geo;
using Ogasela.Infrastructure.Geo;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Geo;

public class GetNearestCityQueryHandlerTests
{
    private static (GetNearestCityQueryHandler Handler, TestApplicationDbContext Db) CreateSut()
    {
        var db = TestApplicationDbContext.Create();
        var lagos = NigeriaState.Create("Lagos", "LA");
        var fct = NigeriaState.Create("FCT", "FC");
        db.NigeriaStates.AddRange(lagos, fct);
        db.NigeriaCities.AddRange(
            NigeriaCity.Create(lagos.Id, "Ikeja", 6.6018m, 3.3515m),
            NigeriaCity.Create(lagos.Id, "Lekki", 6.4698m, 3.5852m),
            NigeriaCity.Create(fct.Id, "Abuja", 9.0579m, 7.4951m),
            // No coordinates yet - never picked.
            NigeriaCity.Create(lagos.Id, "Yaba"));
        db.SaveChanges();
        return (new GetNearestCityQueryHandler(db), db);
    }

    [Fact]
    public async Task Picks_the_closest_city_and_its_state()
    {
        var (handler, _) = CreateSut();

        // Victoria Island - a few km from Lekki, further from Ikeja.
        var result = await handler.Handle(new GetNearestCityQuery(6.4281m, 3.4219m), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.City.Name.Should().Be("Lekki");
        result.Value.State.Name.Should().Be("Lagos");
        result.Value.DistanceKm.Should().BeLessThan(25);
    }

    [Fact]
    public async Task Finds_nothing_far_from_every_city()
    {
        var (handler, _) = CreateSut();

        // London.
        var result = await handler.Handle(new GetNearestCityQuery(51.5072m, -0.1276m), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(GeoErrors.NoCityNearby);
    }

    [Fact]
    public async Task Finds_nothing_when_no_city_has_coordinates()
    {
        var db = TestApplicationDbContext.Create();
        var lagos = NigeriaState.Create("Lagos", "LA");
        db.NigeriaStates.Add(lagos);
        db.NigeriaCities.Add(NigeriaCity.Create(lagos.Id, "Ikeja"));
        db.SaveChanges();

        var result = await new GetNearestCityQueryHandler(db).Handle(new GetNearestCityQuery(6.6m, 3.35m), CancellationToken.None);

        result.Error.Should().Be(GeoErrors.NoCityNearby);
    }

    private static async Task<GetNearestCityQueryHandler> SeededSut()
    {
        var db = TestApplicationDbContext.Create();
        await NigeriaGeoSeeder.SeedAsync(db, CancellationToken.None);
        return new GetNearestCityQueryHandler(db);
    }

    [Fact]
    public async Task Alimosho_lands_in_Lagos_not_across_the_Ogun_border()
    {
        var handler = await SeededSut();

        // Egbeda, Alimosho - Ota (Ogun) is only ~10km away.
        var result = await handler.Handle(new GetNearestCityQuery(6.5920m, 3.2860m), CancellationToken.None);

        result.Value.State.Name.Should().Be("Lagos");
        result.Value.City.Name.Should().Be("Alimosho");
    }

    [Fact]
    public async Task The_devices_state_wins_over_a_closer_city_across_the_border()
    {
        var handler = await SeededSut();

        // Right on the border near Ota: nearest of all is Ota, but the phone says Lagos.
        var position = new GetNearestCityQuery(6.6600m, 3.2400m);
        (await handler.Handle(position, CancellationToken.None)).Value.City.Name.Should().Be("Ota");

        var hinted = await handler.Handle(position with { State = "Lagos State" }, CancellationToken.None);

        hinted.Value.State.Name.Should().Be("Lagos");
    }

    [Theory]
    [InlineData("Lagos", "Lagos")]
    [InlineData("lagos state", "Lagos")]
    [InlineData("Federal Capital Territory", "FCT")]
    [InlineData("Abuja", "FCT")]
    [InlineData("Akwa Ibom State", "Akwa Ibom")]
    public void Matches_geocoder_region_names_to_states(string region, string expected)
    {
        var states = new[] { "Lagos", "FCT", "Akwa Ibom", "Ogun" }.Select(n => NigeriaState.Create(n, n[..2].ToUpper())).ToList();

        GetNearestCityQueryHandler.MatchState(states, region)!.Name.Should().Be(expected);
    }

    [Fact]
    public async Task An_unknown_state_name_falls_back_to_the_nearest_city()
    {
        var handler = await SeededSut();

        var result = await handler.Handle(new GetNearestCityQuery(6.6018m, 3.3515m, "Somewhere"), CancellationToken.None);

        result.Value.City.Name.Should().Be("Ikeja");
    }

    [Fact]
    public async Task Seeder_backfills_missing_coordinates_but_keeps_staff_ones()
    {
        var db = TestApplicationDbContext.Create();
        var lagos = NigeriaState.Create("Lagos", "LA");
        db.NigeriaStates.Add(lagos);
        var ikeja = NigeriaCity.Create(lagos.Id, "Ikeja");
        var lekki = NigeriaCity.Create(lagos.Id, "Lekki", 6.5m, 3.6m);
        db.NigeriaCities.AddRange(ikeja, lekki);
        db.SaveChanges();

        await NigeriaGeoSeeder.SeedAsync(db, CancellationToken.None);

        var cities = await db.NigeriaCities.Where(c => c.StateId == lagos.Id).ToListAsync();
        cities.Single(c => c.Name == "Ikeja").Latitude.Should().Be(6.6018m);
        cities.Single(c => c.Name == "Lekki").Latitude.Should().Be(6.5m);
        (await db.NigeriaCities.CountAsync(c => c.Latitude == null)).Should().Be(0);
    }
}

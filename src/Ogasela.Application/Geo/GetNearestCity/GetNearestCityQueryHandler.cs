using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Geo;
using Ogasela.Shared;

namespace Ogasela.Application.Geo.GetNearestCity;

/// <summary>
/// Picks the city with coordinates closest to a position. The list is small (a few hundred rows
/// at most), so it's measured in memory. Anything further than <see cref="MaxDistanceKm"/> from
/// every city - abroad, or a bad fix - finds nothing, rather than a far-off city.
/// When the device names its state, only that state's cities are considered: near a border the
/// closest known city can be across it (Alimosho, Lagos is nearer Ota, Ogun than most of Lagos).
/// </summary>
public sealed class GetNearestCityQueryHandler : IRequestHandler<GetNearestCityQuery, Result<NearestCityResponse>>
{
    public const double MaxDistanceKm = 150;

    private readonly IApplicationDbContext _dbContext;

    public GetNearestCityQueryHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<NearestCityResponse>> Handle(GetNearestCityQuery request, CancellationToken cancellationToken)
    {
        var cities = await _dbContext.NigeriaCities.AsNoTracking()
            .Where(c => c.Latitude != null && c.Longitude != null)
            .ToListAsync(cancellationToken);
        var states = await _dbContext.NigeriaStates.AsNoTracking().ToListAsync(cancellationToken);

        var measured = cities
            .Select(c => (City: c, Km: DistanceKm(request.Latitude, request.Longitude, c.Latitude!.Value, c.Longitude!.Value)))
            .Where(x => x.Km <= MaxDistanceKm)
            .OrderBy(x => x.Km)
            .ToList();

        // The named state's nearest city if it has one in reach; otherwise the nearest of all.
        var hinted = MatchState(states, request.State);
        var inState = hinted is null ? [] : measured.Where(x => x.City.StateId == hinted.Id).ToList();
        var pick = inState.Count > 0 ? inState : measured;
        if (pick.Count == 0)
        {
            return Result.Failure<NearestCityResponse>(GeoErrors.NoCityNearby);
        }

        var nearest = pick[0];
        var state = states.FirstOrDefault(s => s.Id == nearest.City.StateId);
        if (state is null)
        {
            return Result.Failure<NearestCityResponse>(GeoErrors.NoCityNearby);
        }

        return Result.Success(new NearestCityResponse(
            new StateResponse(state.Id, state.Name, state.Code, state.CreatedAt),
            CityResponse.From(nearest.City),
            Math.Round(nearest.Km, 1)));
    }

    /// <summary>
    /// Matches a geocoder's region name to a state: "Lagos", "Lagos State" and "lagos" all match
    /// Lagos; "Federal Capital Territory" and "Abuja" match FCT.
    /// </summary>
    public static NigeriaState? MatchState(IEnumerable<NigeriaState> states, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Normalize(name);
        if (key is "federal capital territory" or "abuja" or "abuja federal capital territory") key = "fct";
        return states.FirstOrDefault(s => Normalize(s.Name) == key || s.Code.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string name)
    {
        var key = name.Trim().ToLowerInvariant().Replace("(fct)", "").Trim();
        return key.EndsWith(" state") ? key[..^" state".Length].Trim() : key;
    }

    /// <summary>Great-circle (haversine) distance - plenty accurate for "which city is nearest".</summary>
    private static double DistanceKm(decimal lat1, decimal lng1, decimal lat2, decimal lng2)
    {
        const double earthRadiusKm = 6371;
        static double Rad(decimal deg) => (double)deg * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

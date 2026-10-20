using Ogasela.Domain.Geo;

namespace Ogasela.Application.Geo;

public sealed record CityResponse(Guid Id, Guid StateId, string Name, decimal? Latitude, decimal? Longitude, DateTime CreatedAt)
{
    public static CityResponse From(NigeriaCity city) =>
        new(city.Id, city.StateId, city.Name, city.Latitude, city.Longitude, city.CreatedAt);
}

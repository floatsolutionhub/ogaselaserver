using MediatR;
using Ogasela.Shared;

namespace Ogasela.Application.Geo.GetNearestCity;

/// <param name="State">The state the device says it's in (e.g. the phone's reverse geocoder's region), if known.</param>
public sealed record GetNearestCityQuery(decimal Latitude, decimal Longitude, string? State = null) : IRequest<Result<NearestCityResponse>>;

public sealed record NearestCityResponse(StateResponse State, CityResponse City, double DistanceKm);

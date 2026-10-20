using MediatR;
using Ogasela.Shared;

namespace Ogasela.Application.Geo.CreateCity;

public sealed record CreateCityCommand(Guid StateId, string Name, decimal? Latitude = null, decimal? Longitude = null) : IRequest<Result<CityResponse>>;

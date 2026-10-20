using MediatR;
using Ogasela.Shared;

namespace Ogasela.Application.Geo.UpdateCity;

public sealed record UpdateCityCommand(Guid Id, string Name, decimal? Latitude = null, decimal? Longitude = null) : IRequest<Result<CityResponse>>;

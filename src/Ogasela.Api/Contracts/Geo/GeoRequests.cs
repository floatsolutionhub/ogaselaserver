namespace Ogasela.Api.Contracts.Geo;

public sealed record CreateStateRequest(string Name, string Code);

public sealed record UpdateStateRequest(string Name, string Code);

public sealed record CreateCityRequest(Guid StateId, string Name, decimal? Latitude = null, decimal? Longitude = null);

public sealed record UpdateCityRequest(string Name, decimal? Latitude = null, decimal? Longitude = null);

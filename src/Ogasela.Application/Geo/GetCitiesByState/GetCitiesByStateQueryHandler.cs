using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Geo.GetCitiesByState;

public sealed class GetCitiesByStateQueryHandler : IRequestHandler<GetCitiesByStateQuery, Result<IReadOnlyList<CityResponse>>>
{
    private readonly IApplicationDbContext _dbContext;

    public GetCitiesByStateQueryHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<IReadOnlyList<CityResponse>>> Handle(GetCitiesByStateQuery request, CancellationToken cancellationToken)
    {
        var stateExists = await _dbContext.NigeriaStates.AnyAsync(s => s.Id == request.StateId, cancellationToken);
        if (!stateExists)
        {
            return Result.Failure<IReadOnlyList<CityResponse>>(GeoErrors.StateNotFound);
        }

        var cities = await _dbContext.NigeriaCities
            .Where(c => c.StateId == request.StateId)
            .OrderBy(c => c.Name)
            .Select(c => new CityResponse(c.Id, c.StateId, c.Name, c.Latitude, c.Longitude, c.CreatedAt))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<CityResponse>>(cities);
    }
}

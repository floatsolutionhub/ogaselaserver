using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Geo.GetCity;

public sealed class GetCityQueryHandler : IRequestHandler<GetCityQuery, Result<CityResponse>>
{
    private readonly IApplicationDbContext _dbContext;

    public GetCityQueryHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<CityResponse>> Handle(GetCityQuery request, CancellationToken cancellationToken)
    {
        var city = await _dbContext.NigeriaCities.FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken);
        if (city is null)
        {
            return Result.Failure<CityResponse>(GeoErrors.CityNotFound);
        }

        return Result.Success(CityResponse.From(city));
    }
}

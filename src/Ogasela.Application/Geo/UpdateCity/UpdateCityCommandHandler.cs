using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Shared;

namespace Ogasela.Application.Geo.UpdateCity;

public sealed class UpdateCityCommandHandler : IRequestHandler<UpdateCityCommand, Result<CityResponse>>
{
    private readonly IApplicationDbContext _dbContext;

    public UpdateCityCommandHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<CityResponse>> Handle(UpdateCityCommand request, CancellationToken cancellationToken)
    {
        var city = await _dbContext.NigeriaCities.FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken);
        if (city is null)
        {
            return Result.Failure<CityResponse>(GeoErrors.CityNotFound);
        }

        var nameTaken = await _dbContext.NigeriaCities
            .AnyAsync(c => c.Id != request.Id && c.StateId == city.StateId && c.Name == request.Name, cancellationToken);
        if (nameTaken)
        {
            return Result.Failure<CityResponse>(GeoErrors.CityNameTaken);
        }

        city.Update(request.Name, request.Latitude, request.Longitude);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(CityResponse.From(city));
    }
}

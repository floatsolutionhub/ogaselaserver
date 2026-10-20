using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Geo;
using Ogasela.Shared;

namespace Ogasela.Application.Geo.CreateCity;

public sealed class CreateCityCommandHandler : IRequestHandler<CreateCityCommand, Result<CityResponse>>
{
    private readonly IApplicationDbContext _dbContext;

    public CreateCityCommandHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<CityResponse>> Handle(CreateCityCommand request, CancellationToken cancellationToken)
    {
        var stateExists = await _dbContext.NigeriaStates.AnyAsync(s => s.Id == request.StateId, cancellationToken);
        if (!stateExists)
        {
            return Result.Failure<CityResponse>(GeoErrors.StateNotFound);
        }

        var nameTaken = await _dbContext.NigeriaCities
            .AnyAsync(c => c.StateId == request.StateId && c.Name == request.Name, cancellationToken);
        if (nameTaken)
        {
            return Result.Failure<CityResponse>(GeoErrors.CityNameTaken);
        }

        var city = NigeriaCity.Create(request.StateId, request.Name, request.Latitude, request.Longitude);
        _dbContext.NigeriaCities.Add(city);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(CityResponse.From(city));
    }
}

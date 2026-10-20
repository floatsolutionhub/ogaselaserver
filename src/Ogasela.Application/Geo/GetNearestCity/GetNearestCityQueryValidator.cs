using FluentValidation;

namespace Ogasela.Application.Geo.GetNearestCity;

public sealed class GetNearestCityQueryValidator : AbstractValidator<GetNearestCityQuery>
{
    public GetNearestCityQueryValidator()
    {
        RuleFor(x => x.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(x => x.Longitude).InclusiveBetween(-180m, 180m);
        RuleFor(x => x.State).MaximumLength(100);
    }
}

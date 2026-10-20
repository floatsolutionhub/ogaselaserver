using FluentValidation;

namespace Ogasela.Application.Geo.UpdateCity;

public sealed class UpdateCityCommandValidator : AbstractValidator<UpdateCityCommand>
{
    public UpdateCityCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        // Both or neither, and somewhere on Earth.
        RuleFor(x => x.Latitude).NotNull().When(x => x.Longitude is not null).InclusiveBetween(-90m, 90m);
        RuleFor(x => x.Longitude).NotNull().When(x => x.Latitude is not null).InclusiveBetween(-180m, 180m);
    }
}

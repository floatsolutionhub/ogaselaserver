using FluentValidation;

namespace Ogasela.Application.Geo.CreateCity;

public sealed class CreateCityCommandValidator : AbstractValidator<CreateCityCommand>
{
    public CreateCityCommandValidator()
    {
        RuleFor(x => x.StateId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        // Both or neither, and somewhere on Earth.
        RuleFor(x => x.Latitude).NotNull().When(x => x.Longitude is not null).InclusiveBetween(-90m, 90m);
        RuleFor(x => x.Longitude).NotNull().When(x => x.Latitude is not null).InclusiveBetween(-180m, 180m);
    }
}

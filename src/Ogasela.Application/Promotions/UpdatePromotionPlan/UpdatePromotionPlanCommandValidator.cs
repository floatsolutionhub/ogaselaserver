using Ogasela.Domain.Listings;
using FluentValidation;

namespace Ogasela.Application.Promotions.UpdatePromotionPlan;

internal static class PromotionPlanRules
{
    public const int MaxFeatures = 10;

    public static void Apply<T>(
        AbstractValidator<T> validator,
        Func<T, string> name,
        Func<T, string?> description,
        Func<T, IReadOnlyList<string>> features,
        Func<T, int> durationDays,
        Func<T, int> photoLimit,
        Func<T, int> boostWeight,
        Func<T, decimal> price,
        Func<T, int> bundledCredit)
    {
        validator.RuleFor(x => name(x)).NotEmpty().WithMessage("Give the plan a name.").MaximumLength(40).WithName("Name");
        validator.RuleFor(x => description(x)).MaximumLength(300).WithName("Description");
        validator.RuleFor(x => features(x)).NotNull()
            .Must(f => f.Count <= MaxFeatures).WithMessage($"List at most {MaxFeatures} things the plan offers.")
            .WithName("Features");
        validator.RuleForEach(x => features(x)).MaximumLength(80).WithName("Feature");
        validator.RuleFor(x => durationDays(x)).InclusiveBetween(1, 365).WithName("DurationDays");
        validator.RuleFor(x => photoLimit(x)).InclusiveBetween(1, Listing.MaxPhotoCount).WithName("PhotoLimit");
        validator.RuleFor(x => boostWeight(x)).InclusiveBetween(0, 10).WithName("BoostWeight");
        validator.RuleFor(x => price(x)).GreaterThanOrEqualTo(0).WithName("Price");
        validator.RuleFor(x => bundledCredit(x)).GreaterThanOrEqualTo(0).WithName("BundledAdCreditKobo");
    }
}

public sealed class UpdatePromotionPlanCommandValidator : AbstractValidator<UpdatePromotionPlanCommand>
{
    public UpdatePromotionPlanCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.AiToolTier).IsInEnum();
        PromotionPlanRules.Apply(this, x => x.Name, x => x.Description, x => x.Features, x => x.DurationDays,
            x => x.PhotoLimit, x => x.BoostWeight, x => x.Price, x => x.BundledAdCreditKobo);
    }
}

public sealed class CreatePromotionPlanCommandValidator : AbstractValidator<CreatePromotionPlanCommand>
{
    public CreatePromotionPlanCommandValidator()
    {
        RuleFor(x => x.AiToolTier).IsInEnum();
        PromotionPlanRules.Apply(this, x => x.Name, x => x.Description, x => x.Features, x => x.DurationDays,
            x => x.PhotoLimit, x => x.BoostWeight, x => x.Price, x => x.BundledAdCreditKobo);
    }
}

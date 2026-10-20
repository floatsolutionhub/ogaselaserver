using Ogasela.Shared;

namespace Ogasela.Application.Listings;

public static class ListingErrors
{
    public static readonly Error SellerProfileNotFound = new(
        "Listing.SellerProfileNotFound", "No seller profile was found for the current user.");

    public static readonly Error ListingNotFound = new(
        "Listing.NotFound", "The listing could not be found.");

    public static readonly Error NotOwner = new(
        "Listing.NotOwner", "You do not own this listing.");

    public static readonly Error CategoryNotFound = new(
        "Listing.CategoryNotFound", "The specified category could not be found.");

    public static readonly Error PromotionPlanNotFound = new(
        "Listing.PromotionPlanNotFound", "The specified promotion plan could not be found.");

    public static readonly Error MediaLimitExceeded = new(
        "Listing.MediaLimitExceeded", "The number of media items exceeds the selected plan's photo limit.");

    public static readonly Error InvalidStatusForUpdate = new(
        "Listing.InvalidStatusForUpdate", "A listing can only be edited while it is a Draft or Active.");

    public static readonly Error InvalidStatusForPublish = new(
        "Listing.InvalidStatusForPublish", "Only a Draft listing can be published. Use repost for a listing that has already been published before.");

    public static readonly Error InvalidStatusForRepost = new(
        "Listing.InvalidStatusForRepost", "Only an Expired, Sold, or Paused listing can be reposted.");

    public static readonly Error InvalidStatusForPause = new(
        "Listing.InvalidStatusForPause", "Only an Active or ExpiringSoon listing can be paused.");

    public static readonly Error InvalidStatusForMarkSold = new(
        "Listing.InvalidStatusForMarkSold", "Only an Active, ExpiringSoon, or Paused listing can be marked sold.");

    // RULE-01 through RULE-06 from PRS Section 5 (RULE-03, the media-limit check, is instead
    // enforced up front on Create/Update - see MediaLimitExceeded above).
    public static readonly Error NotVerified = new(
        "Listing.NotVerified", "The seller must complete biometric verification before publishing.");

    public static readonly Error NoPlanSelected = new(
        "Listing.NoPlanSelected", "A promotion plan must be selected before publishing.");

    public static readonly Error CategoryNotFreeEligible = new(
        "Listing.CategoryNotFreeEligible", "This category is not eligible for the Free plan.");

    public static readonly Error FreePlanCapReached = new(
        "Listing.FreePlanCapReached", "You have reached the maximum number of active Free-plan listings.");

    public static readonly Error PaymentFailed = new(
        "Listing.PaymentFailed", "Payment for the selected plan was not authorized.");

    public static readonly Error Incomplete = new(
        "Listing.Incomplete", "Finish your ad first - it needs a title, description and at least one photo.");

    public static readonly Error PlanNotOnSale = new(
        "Listing.PlanNotOnSale", "That promotion plan isn't available any more. Choose another.");

    public static readonly Error PaymentNotFound = new(
        "Listing.PaymentNotFound", "We couldn't find that payment for this ad.");

    public static readonly Error InvalidStatusForDelete = new(
        "Listing.InvalidStatusForDelete", "Only a draft, rejected, pending or expired ad can be deleted.");

    public static readonly Error ResubmitInstead = new(
        "Listing.ResubmitInstead", "This ad's plan is still running. Resubmit your changes for review instead of paying again.");

    public static readonly Error NothingToResubmit = new(
        "Listing.NothingToResubmit", "Only an edited ad whose plan is still running can be resubmitted. Renew it with a plan instead.");

    public static readonly Error NotLive = new(
        "Listing.NotLive", "Only an ad that's live (or being edited while its plan is running) can change plan.");

    public static readonly Error PlanDowngradeNotAllowed = new(
        "Listing.PlanDowngradeNotAllowed", "A live ad's plan can't be downgraded. Choose a plan at least as high as your current one.");

    public static readonly Error SamePlan = new(
        "Listing.SamePlan", "Your ad is already on that plan.");

    public static readonly Error PlanChangeNotAllowedInEdit = new(
        "Listing.PlanChangeNotAllowedInEdit", "To change a live ad's plan, upgrade it with a plan payment.");

    public static readonly Error UnsupportedImage = new(
        "Listing.UnsupportedImage", "That photo's format isn't supported. Use a JPEG, PNG or WebP photo.");
}

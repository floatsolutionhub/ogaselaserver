using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Listings;
using Ogasela.Domain.OfficePickups;
using Ogasela.Shared;

namespace Ogasela.Application.OfficePickups;

/// <summary>
/// The fixed money rules of office pickup. These percentages are quoted to users in the app and
/// in the Terms of Service ("Office Pickup") - change all three places together.
/// </summary>
public static class OfficePickupRules
{
    /// <summary>Added on top of the ad's price: 3%.</summary>
    public const decimal ServiceFeeRate = 0.03m;

    /// <summary>Kept from a refund (failed inspection or the buyer backing out): 1% of the item price.</summary>
    public const decimal CancellationFeeRate = 0.01m;

    // No 0/O/1/I, so a reference read out over the phone can't be misheard.
    private const string ReferenceAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NewReference() =>
        "OPK-" + new string(Enumerable.Range(0, 6).Select(_ => ReferenceAlphabet[RandomNumberGenerator.GetInt32(ReferenceAlphabet.Length)]).ToArray());

    /// <summary>Listing prices are in naira; every office pickup amount is in kobo, rounded to the whole kobo.</summary>
    public static decimal ToKobo(decimal naira) => decimal.Round(naira * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal ServiceFeeKobo(decimal itemPriceKobo) =>
        decimal.Round(itemPriceKobo * ServiceFeeRate, 0, MidpointRounding.AwayFromZero);

    public static decimal CancellationFeeKobo(decimal itemPriceKobo) =>
        decimal.Round(itemPriceKobo * CancellationFeeRate, 0, MidpointRounding.AwayFromZero);

    public static string FormatNaira(decimal kobo) => "₦" + (kobo / 100m).ToString("#,##0.##");

    /// <summary>The label buyers, sellers and staff see for each stage.</summary>
    public static string StatusLabel(OfficePickupStatus status) => status switch
    {
        OfficePickupStatus.AwaitingPayment => "Awaiting payment",
        OfficePickupStatus.PaymentFailed => "Payment failed",
        OfficePickupStatus.PaymentCancelled => "Payment cancelled",
        OfficePickupStatus.PaymentConfirmed => "Payment confirmed",
        OfficePickupStatus.ItemReceived => "Item delivered to our office",
        OfficePickupStatus.InspectionPassed => "Item passed quality inspection",
        OfficePickupStatus.InspectionFailed => "Item failed quality inspection",
        OfficePickupStatus.ReadyForCollection => "Item available for pickup or delivery",
        OfficePickupStatus.OutForDelivery => "Out for delivery",
        OfficePickupStatus.Completed => "Completed",
        _ => "Refunded"
    };

    /// <summary>
    /// Whether buyers can request office pickup for this ad: it's live, has a price, and its
    /// category (or that category's parent) has office pickup switched on in the Control Portal.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(IApplicationDbContext dbContext, Listing listing, CancellationToken cancellationToken)
    {
        if (listing.Status is not (ListingStatus.Active or ListingStatus.ExpiringSoon) || listing.Price is not > 0)
        {
            return false;
        }

        var category = await dbContext.Categories
            .Where(c => c.Id == listing.CategoryId)
            .Select(c => new { c.IsOfficePickupEligible, c.ParentCategoryId })
            .FirstOrDefaultAsync(cancellationToken);
        if (category is null)
        {
            return false;
        }

        return category.IsOfficePickupEligible
               || (category.ParentCategoryId is { } parentId
                   && await dbContext.Categories.AnyAsync(c => c.Id == parentId && c.IsOfficePickupEligible, cancellationToken));
    }
}

public static class OfficePickupErrors
{
    public static readonly Error NotFound = new("OfficePickup.NotFound", "This office pickup request could not be found.");
    public static readonly Error ListingNotFound = new("OfficePickup.ListingNotFound", "That ad could not be found.");
    public static readonly Error NotAvailable = new(
        "OfficePickup.NotAvailable", "Office pickup isn't available for this ad.");
    public static readonly Error OwnListing = new("OfficePickup.OwnListing", "You can't request office pickup for your own ad.");
    public static readonly Error BranchNotFound = new("OfficePickup.BranchNotFound", "That branch isn't available. Choose another one.");
    public static readonly Error AlreadyRequested = new(
        "OfficePickup.AlreadyRequested", "You already have an office pickup in progress for this ad.");
    public static readonly Error NotAwaitingPayment = new(
        "OfficePickup.NotAwaitingPayment", "This request isn't waiting for payment.");
    public static readonly Error PaymentNotStarted = new(
        "OfficePickup.PaymentNotStarted", "No payment has been started for this request.");
    public static readonly Error NotCancellable = new(
        "OfficePickup.NotCancellable", "This request can't be cancelled at this stage.");
    public static readonly Error CancellationAlreadyRequested = new(
        "OfficePickup.CancellationAlreadyRequested", "You've already asked to cancel this request. Our team will be in touch.");
    public static readonly Error InvalidTransition = new(
        "OfficePickup.InvalidTransition", "The request can't move to that stage from where it is now.");
    public static readonly Error DeliveryAddressRequired = new(
        "OfficePickup.DeliveryAddressRequired", "Enter the buyer's delivery address.");
    public static readonly Error NotRefundable = new(
        "OfficePickup.NotRefundable", "Only a paid request that hasn't been completed can be refunded.");
    public static readonly Error RefundFailed = new(
        "OfficePickup.RefundFailed", "The payment provider couldn't process the refund. Nothing was changed - try again.");
    public static readonly Error ConcurrentUpdate = new(
        "OfficePickup.ConcurrentUpdate", "Someone else updated this request at the same time. Reload it and try again.");
}

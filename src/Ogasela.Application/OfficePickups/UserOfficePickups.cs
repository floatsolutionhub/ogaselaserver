using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Payments;
using Ogasela.Domain.Listings;
using Ogasela.Domain.OfficePickups;
using Ogasela.Shared;

namespace Ogasela.Application.OfficePickups;

/// <summary>Active branches, nearest to (Latitude, Longitude) first when given; otherwise by state and name.</summary>
public sealed record GetOfficeBranchesQuery(decimal? Latitude, decimal? Longitude) : IRequest<Result<IReadOnlyList<OfficeBranchResponse>>>;

/// <summary>What office pickup would cost for an ad, for the explainer sheet. Amounts in kobo.</summary>
public sealed record GetOfficePickupQuoteQuery(Guid ListingId) : IRequest<Result<OfficePickupQuote>>;

/// <summary>Creates the request and starts the gateway checkout: open RedirectUrl, then confirm.</summary>
public sealed record StartOfficePickupCommand(Guid ListingId, Guid BranchId) : IRequest<Result<OfficePickupCheckoutResponse>>;

/// <summary>Called when the buyer returns from the payment page. Asks the gateway directly, then settles like the webhook would. Safe to repeat.</summary>
public sealed record ConfirmOfficePickupPaymentCommand(Guid RequestId) : IRequest<Result<OfficePickupDetail>>;

/// <summary>
/// Called when the buyer closes the payment page without returning through the success page. Asks the
/// gateway first: a payment that went through is settled as normal, one still in progress at the bank
/// stays awaiting payment, and only one that was never paid is recorded as cancelled.
/// </summary>
public sealed record CancelOfficePickupPaymentCommand(Guid RequestId) : IRequest<Result<OfficePickupDetail>>;

/// <summary>Paid requests the caller is the buyer or seller on, most recent first.</summary>
public sealed record GetMyOfficePickupsQuery : IRequest<Result<IReadOnlyList<OfficePickupSummary>>>;

public sealed record GetMyOfficePickupQuery(Guid RequestId) : IRequest<Result<OfficePickupDetail>>;

/// <summary>The buyer asking to back out. Staff process the refund (less the cancellation fee) from the Control Portal.</summary>
public sealed record RequestOfficePickupCancellationCommand(Guid RequestId, string Reason) : IRequest<Result<OfficePickupDetail>>;

public sealed record OfficeBranchResponse(
    Guid Id,
    string Name,
    string Address,
    string State,
    string? City,
    string? Phone,
    string? OpeningHours,
    decimal Latitude,
    decimal Longitude,
    double? DistanceKm);

public sealed record OfficePickupQuote(
    bool Available,
    decimal ItemPriceKobo,
    decimal ServiceFeeKobo,
    decimal TotalKobo,
    decimal CancellationFeeKobo,
    decimal ServiceFeePercent,
    decimal CancellationFeePercent,
    bool IsOwnListing = false);

public sealed record OfficePickupCheckoutResponse(OfficePickupSummary Request, string PaymentReference, string RedirectUrl);

/// <summary>MyRole is "Buyer" or "Seller". Amounts in kobo.</summary>
public sealed record OfficePickupSummary(
    Guid Id,
    string Reference,
    Guid ListingId,
    string ListingTitle,
    string? ListingThumbnailUrl,
    string MyRole,
    OfficePickupStatus Status,
    string StatusLabel,
    decimal TotalKobo,
    string BranchName,
    bool CancellationRequested,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record OfficePickupActivityResponse(OfficePickupStatus Status, string Title, string? Note, DateTime CreatedAt);

public sealed record OfficePickupDetail(
    OfficePickupSummary Summary,
    decimal ItemPriceKobo,
    decimal ServiceFeeKobo,
    DateTime? PaidAt,
    OfficeBranchResponse Branch,
    OfficePickupFulfilment? Fulfilment,
    string? DeliveryAddress,
    DateTime? CancellationRequestedAt,
    string? CancellationReason,
    decimal? CancellationFeeKobo,
    decimal? RefundedKobo,
    DateTime? RefundedAt,
    DateTime? CompletedAt,
    IReadOnlyList<OfficePickupActivityResponse> Activities);

public sealed class StartOfficePickupCommandValidator : AbstractValidator<StartOfficePickupCommand>
{
    public StartOfficePickupCommandValidator()
    {
        RuleFor(x => x.ListingId).NotEmpty();
        RuleFor(x => x.BranchId).NotEmpty().WithMessage("Choose the branch you'll pick the item up from.");
    }
}

public sealed class RequestOfficePickupCancellationCommandValidator : AbstractValidator<RequestOfficePickupCancellationCommand>
{
    public RequestOfficePickupCancellationCommandValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Tell us why you'd like to cancel.").MaximumLength(1000);
}

public sealed class UserOfficePickupHandlers :
    IRequestHandler<GetOfficeBranchesQuery, Result<IReadOnlyList<OfficeBranchResponse>>>,
    IRequestHandler<GetOfficePickupQuoteQuery, Result<OfficePickupQuote>>,
    IRequestHandler<StartOfficePickupCommand, Result<OfficePickupCheckoutResponse>>,
    IRequestHandler<ConfirmOfficePickupPaymentCommand, Result<OfficePickupDetail>>,
    IRequestHandler<CancelOfficePickupPaymentCommand, Result<OfficePickupDetail>>,
    IRequestHandler<GetMyOfficePickupsQuery, Result<IReadOnlyList<OfficePickupSummary>>>,
    IRequestHandler<GetMyOfficePickupQuery, Result<OfficePickupDetail>>,
    IRequestHandler<RequestOfficePickupCancellationCommand, Result<OfficePickupDetail>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly OfficePickupPaymentSettlement _settlement;
    private readonly IDateTime _dateTime;

    public UserOfficePickupHandlers(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IPaymentGatewayResolver gatewayResolver,
        OfficePickupPaymentSettlement settlement, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _gatewayResolver = gatewayResolver;
        _settlement = settlement;
        _dateTime = dateTime;
    }

    private Guid Me => _currentUser.UserId!.Value;

    public async Task<Result<IReadOnlyList<OfficeBranchResponse>>> Handle(GetOfficeBranchesQuery request, CancellationToken cancellationToken)
    {
        var branches = await _dbContext.OfficeBranches.AsNoTracking().Where(b => b.IsActive).ToListAsync(cancellationToken);
        var hasOrigin = request.Latitude is not null && request.Longitude is not null;
        var rows = branches
            .Select(b => ToBranch(b, hasOrigin ? DistanceKm(request.Latitude!.Value, request.Longitude!.Value, b.Latitude, b.Longitude) : null))
            .OrderBy(b => b.DistanceKm ?? 0)
            .ThenBy(b => b.State)
            .ThenBy(b => b.Name)
            .ToList();
        return Result.Success<IReadOnlyList<OfficeBranchResponse>>(rows);
    }

    public async Task<Result<OfficePickupQuote>> Handle(GetOfficePickupQuoteQuery request, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null || listing.Status is ListingStatus.Draft or ListingStatus.PendingReview)
        {
            return Result.Failure<OfficePickupQuote>(OfficePickupErrors.ListingNotFound);
        }

        var available = await OfficePickupRules.IsAvailableAsync(_dbContext, listing, cancellationToken);
        // A seller can't office-pickup their own ad, so the app hides the option from them (e.g. in chat).
        var isOwnListing = await _dbContext.SellerProfiles.AnyAsync(s => s.Id == listing.SellerId && s.UserId == Me, cancellationToken);
        var item = OfficePickupRules.ToKobo(listing.Price ?? 0);
        var fee = OfficePickupRules.ServiceFeeKobo(item);
        return Result.Success(new OfficePickupQuote(
            available && !isOwnListing, item, fee, item + fee, OfficePickupRules.CancellationFeeKobo(item),
            OfficePickupRules.ServiceFeeRate * 100, OfficePickupRules.CancellationFeeRate * 100, isOwnListing));
    }

    public async Task<Result<OfficePickupCheckoutResponse>> Handle(StartOfficePickupCommand request, CancellationToken cancellationToken)
    {
        var listing = await _dbContext.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.ListingId, cancellationToken);
        if (listing is null)
        {
            return Result.Failure<OfficePickupCheckoutResponse>(OfficePickupErrors.ListingNotFound);
        }

        var sellerUserId = await _dbContext.SellerProfiles
            .Where(s => s.Id == listing.SellerId)
            .Select(s => (Guid?)s.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (sellerUserId is null)
        {
            return Result.Failure<OfficePickupCheckoutResponse>(OfficePickupErrors.ListingNotFound);
        }

        if (sellerUserId == Me)
        {
            return Result.Failure<OfficePickupCheckoutResponse>(OfficePickupErrors.OwnListing);
        }

        if (!await OfficePickupRules.IsAvailableAsync(_dbContext, listing, cancellationToken))
        {
            return Result.Failure<OfficePickupCheckoutResponse>(OfficePickupErrors.NotAvailable);
        }

        var branch = await _dbContext.OfficeBranches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == request.BranchId && b.IsActive, cancellationToken);
        if (branch is null)
        {
            return Result.Failure<OfficePickupCheckoutResponse>(OfficePickupErrors.BranchNotFound);
        }

        var inProgress = await _dbContext.OfficePickupRequests.AnyAsync(
            r => r.ListingId == listing.Id && r.BuyerUserId == Me
                 && r.Status != OfficePickupStatus.AwaitingPayment && r.Status != OfficePickupStatus.PaymentFailed
                 && r.Status != OfficePickupStatus.PaymentCancelled && r.Status != OfficePickupStatus.Completed && r.Status != OfficePickupStatus.Refunded,
            cancellationToken);
        if (inProgress)
        {
            return Result.Failure<OfficePickupCheckoutResponse>(OfficePickupErrors.AlreadyRequested);
        }

        var reference = OfficePickupRules.NewReference();
        while (await _dbContext.OfficePickupRequests.AnyAsync(r => r.Reference == reference, cancellationToken))
        {
            reference = OfficePickupRules.NewReference();
        }

        var item = OfficePickupRules.ToKobo(listing.Price!.Value);
        var pickup = OfficePickupRequest.Create(
            reference, listing.Id, listing.Title, Me, sellerUserId.Value, branch.Id, item, OfficePickupRules.ServiceFeeKobo(item),
            _dateTime.UtcNow);

        var email = await _dbContext.Users.Where(u => u.Id == Me).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
        var init = await _gatewayResolver.GetActive().InitializeCustomerTransactionAsync(
            // The gateway needs a well-formed email even for phone-only accounts.
            string.IsNullOrWhiteSpace(email) ? $"buyer+{Me:N}@ogasela.com" : email,
            pickup.TotalKobo, $"Ogasela office pickup {reference} for \"{listing.Title}\"", cancellationToken);
        if (init.IsFailure)
        {
            return Result.Failure<OfficePickupCheckoutResponse>(PaymentErrors.GatewayError);
        }

        pickup.AttachPayment(init.Value.Reference, _dateTime.UtcNow);
        _dbContext.OfficePickupRequests.Add(pickup);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var summary = (await SummariesAsync(r => r.Id == pickup.Id, cancellationToken)).Single();
        return Result.Success(new OfficePickupCheckoutResponse(summary, init.Value.Reference, init.Value.RedirectUrl));
    }

    public Task<Result<OfficePickupDetail>> Handle(ConfirmOfficePickupPaymentCommand request, CancellationToken cancellationToken) =>
        VerifyPaymentAsync(request.RequestId, cancelIfUnpaid: false, cancellationToken);

    public Task<Result<OfficePickupDetail>> Handle(CancelOfficePickupPaymentCommand request, CancellationToken cancellationToken) =>
        VerifyPaymentAsync(request.RequestId, cancelIfUnpaid: true, cancellationToken);

    private async Task<Result<OfficePickupDetail>> VerifyPaymentAsync(Guid requestId, bool cancelIfUnpaid, CancellationToken cancellationToken)
    {
        var pickup = await _dbContext.OfficePickupRequests.FirstOrDefaultAsync(
            r => r.Id == requestId && r.BuyerUserId == Me, cancellationToken);
        if (pickup is null)
        {
            return Result.Failure<OfficePickupDetail>(OfficePickupErrors.NotFound);
        }

        if (pickup.Status == OfficePickupStatus.AwaitingPayment)
        {
            if (pickup.PaymentReference is not { } reference)
            {
                return Result.Failure<OfficePickupDetail>(OfficePickupErrors.PaymentNotStarted);
            }

            var verify = await _gatewayResolver.GetActive().VerifyTransactionAsync(reference, cancellationToken);
            if (verify.IsFailure)
            {
                return Result.Failure<OfficePickupDetail>(PaymentErrors.GatewayError);
            }

            if (verify.Value.Successful)
            {
                await _settlement.SettleAsync(pickup, verify.Value, cancellationToken);
            }
            else if (cancelIfUnpaid && verify.Value.State == PaymentAttemptState.Abandoned)
            {
                var now = _dateTime.UtcNow;
                pickup.MarkPaymentCancelled(now);
                _dbContext.OfficePickupActivities.Add(OfficePickupActivity.Create(
                    pickup.Id, OfficePickupStatus.PaymentCancelled, OfficePickupRules.StatusLabel(OfficePickupStatus.PaymentCancelled),
                    "You left the payment page without paying, so nothing was charged.", Me, now));
                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // The webhook settled it at the same moment - report whatever it decided.
                }
            }
            else if (cancelIfUnpaid && verify.Value.State == PaymentAttemptState.Failed)
            {
                await _settlement.SettleAsync(pickup, verify.Value, cancellationToken);
            }

            // Otherwise it's still in progress at the bank: leave it awaiting payment for the webhook.
        }

        return await DetailAsync(pickup.Id, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<OfficePickupSummary>>> Handle(GetMyOfficePickupsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await SummariesAsync(
            r => (r.BuyerUserId == Me || r.SellerUserId == Me)
                 && r.Status != OfficePickupStatus.AwaitingPayment && r.Status != OfficePickupStatus.PaymentFailed
                 && r.Status != OfficePickupStatus.PaymentCancelled,
            cancellationToken));

    public async Task<Result<OfficePickupDetail>> Handle(GetMyOfficePickupQuery request, CancellationToken cancellationToken)
    {
        var visible = await _dbContext.OfficePickupRequests.AnyAsync(
            r => r.Id == request.RequestId && (r.BuyerUserId == Me || r.SellerUserId == Me), cancellationToken);
        return visible ? await DetailAsync(request.RequestId, cancellationToken) : Result.Failure<OfficePickupDetail>(OfficePickupErrors.NotFound);
    }

    public async Task<Result<OfficePickupDetail>> Handle(RequestOfficePickupCancellationCommand request, CancellationToken cancellationToken)
    {
        var pickup = await _dbContext.OfficePickupRequests.FirstOrDefaultAsync(
            r => r.Id == request.RequestId && r.BuyerUserId == Me, cancellationToken);
        if (pickup is null)
        {
            return Result.Failure<OfficePickupDetail>(OfficePickupErrors.NotFound);
        }

        if (!pickup.IsInProgress || pickup.Status == OfficePickupStatus.OutForDelivery)
        {
            return Result.Failure<OfficePickupDetail>(OfficePickupErrors.NotCancellable);
        }

        if (pickup.CancellationRequestedAt is not null)
        {
            return Result.Failure<OfficePickupDetail>(OfficePickupErrors.CancellationAlreadyRequested);
        }

        var now = _dateTime.UtcNow;
        var reason = request.Reason.Trim();
        pickup.RequestCancellation(reason, now);
        _dbContext.OfficePickupActivities.Add(OfficePickupActivity.Create(
            pickup.Id, pickup.Status, "Cancellation requested",
            $"Our team will process your refund of {OfficePickupRules.FormatNaira(pickup.TotalKobo - OfficePickupRules.CancellationFeeKobo(pickup.ItemPriceKobo))} (less the 1% cancellation fee).",
            Me, now));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<OfficePickupDetail>(OfficePickupErrors.ConcurrentUpdate);
        }

        return await DetailAsync(pickup.Id, cancellationToken);
    }

    private async Task<Result<OfficePickupDetail>> DetailAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var pickup = await _dbContext.OfficePickupRequests.AsNoTracking().FirstAsync(r => r.Id == requestId, cancellationToken);
        var summary = (await SummariesAsync(r => r.Id == requestId, cancellationToken)).Single();
        var branch = await _dbContext.OfficeBranches.AsNoTracking().FirstAsync(b => b.Id == pickup.BranchId, cancellationToken);
        var activities = await _dbContext.OfficePickupActivities.AsNoTracking()
            .Where(a => a.RequestId == requestId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new OfficePickupActivityResponse(a.Status, a.Title, a.Note, a.CreatedAt))
            .ToListAsync(cancellationToken);

        return Result.Success(new OfficePickupDetail(
            summary,
            pickup.ItemPriceKobo,
            pickup.ServiceFeeKobo,
            pickup.PaidAt,
            ToBranch(branch, null),
            pickup.Fulfilment,
            pickup.DeliveryAddress,
            pickup.CancellationRequestedAt,
            pickup.CancellationReason,
            pickup.CancellationFeeKobo,
            pickup.RefundedKobo,
            pickup.RefundedAt,
            pickup.CompletedAt,
            activities));
    }

    private async Task<IReadOnlyList<OfficePickupSummary>> SummariesAsync(
        System.Linq.Expressions.Expression<Func<OfficePickupRequest, bool>> filter, CancellationToken cancellationToken)
    {
        var rows = await (
            from r in _dbContext.OfficePickupRequests.AsNoTracking().Where(filter)
            join b in _dbContext.OfficeBranches on r.BranchId equals b.Id
            join l in _dbContext.Listings on r.ListingId equals l.Id into listingJoin
            from l in listingJoin.DefaultIfEmpty()
            orderby r.CreatedAt descending, r.Id descending
            select new { r, BranchName = b.Name, MediaUrls = l == null ? null : l.MediaUrls })
            .ToListAsync(cancellationToken);

        return rows.Select(x => new OfficePickupSummary(
            x.r.Id,
            x.r.Reference,
            x.r.ListingId,
            x.r.ListingTitle,
            x.MediaUrls?.FirstOrDefault(),
            x.r.BuyerUserId == Me ? "Buyer" : "Seller",
            x.r.Status,
            OfficePickupRules.StatusLabel(x.r.Status),
            x.r.TotalKobo,
            x.BranchName,
            x.r.CancellationRequestedAt is not null,
            x.r.CreatedAt,
            x.r.UpdatedAt)).ToList();
    }

    internal static OfficeBranchResponse ToBranch(OfficeBranch b, double? distanceKm) => new(
        b.Id, b.Name, b.Address, b.State, b.City, b.Phone, b.OpeningHours, b.Latitude, b.Longitude,
        distanceKm is { } d ? Math.Round(d, 1) : null);

    /// <summary>Great-circle (haversine) distance - plenty accurate for "which branch is nearest".</summary>
    private static double DistanceKm(decimal lat1, decimal lng1, decimal lat2, decimal lng2)
    {
        const double earthRadiusKm = 6371;
        static double Rad(decimal deg) => (double)deg * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

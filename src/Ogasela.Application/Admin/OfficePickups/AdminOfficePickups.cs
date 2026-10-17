using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.OfficePickups;
using Ogasela.Application.Payments;
using Ogasela.Domain.Listings;
using Ogasela.Domain.OfficePickups;
using Ogasela.Shared;

namespace Ogasela.Application.Admin.OfficePickups;

/// <summary>
/// Office pickups for the Control Portal, newest first. Unpaid checkouts (AwaitingPayment /
/// PaymentFailed / PaymentCancelled) are hidden unless IncludeUnpaid. Search matches the reference, the ad title or
/// either party's name.
/// </summary>
public sealed record GetAdminOfficePickupsQuery(
    OfficePickupStatus? Status, Guid? BranchId, bool CancellationRequested, bool IncludeUnpaid, string? Search, int Page, int PageSize)
    : IRequest<Result<AdminOfficePickupPage>>;

public sealed record AdminOfficePickupListItem(
    Guid Id,
    string Reference,
    string ListingTitle,
    string? BuyerName,
    string? SellerName,
    string BranchName,
    OfficePickupStatus Status,
    string StatusLabel,
    decimal TotalKobo,
    bool CancellationRequested,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AdminOfficePickupPage(IReadOnlyList<AdminOfficePickupListItem> Items, int Page, int PageSize, int TotalCount);

public sealed record GetAdminOfficePickupQuery(Guid RequestId) : IRequest<Result<AdminOfficePickupDetail>>;

public sealed record AdminOfficePickupParty(Guid UserId, string? Name, string? Email, string? Phone);

public sealed record AdminOfficePickupActivity(OfficePickupStatus Status, string Title, string? Note, string? ActorName, DateTime CreatedAt);

/// <summary>NextStatuses is what the request can move to now; RefundPreviewKobo/CancellationFeePreviewKobo are what a refund would pay out and keep.</summary>
public sealed record AdminOfficePickupDetail(
    Guid Id,
    string Reference,
    Guid ListingId,
    string ListingTitle,
    ListingStatus? ListingStatus,
    string? ListingThumbnailUrl,
    OfficePickupStatus Status,
    string StatusLabel,
    decimal ItemPriceKobo,
    decimal ServiceFeeKobo,
    decimal TotalKobo,
    string? PaymentReference,
    DateTime? PaidAt,
    OfficeBranchResponse Branch,
    AdminOfficePickupParty Buyer,
    AdminOfficePickupParty Seller,
    OfficePickupFulfilment? Fulfilment,
    string? DeliveryAddress,
    DateTime? CancellationRequestedAt,
    string? CancellationReason,
    decimal? CancellationFeeKobo,
    decimal? RefundedKobo,
    DateTime? RefundedAt,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    bool CanRefund,
    decimal RefundPreviewKobo,
    decimal CancellationFeePreviewKobo,
    IReadOnlyList<OfficePickupStatus> NextStatuses,
    IReadOnlyList<AdminOfficePickupActivity> Activities);

/// <summary>Moves the request to its next stage. DeliveryAddress is required for OutForDelivery. Note is shown to the buyer and seller.</summary>
public sealed record UpdateOfficePickupStatusCommand(Guid RequestId, OfficePickupStatus Status, string? Note, string? DeliveryAddress)
    : IRequest<Result>;

/// <summary>Refunds the buyer the total paid less the 1% cancellation fee, back through the payment gateway, and closes the request.</summary>
public sealed record RefundOfficePickupCommand(Guid RequestId, string Reason) : IRequest<Result>;

public sealed record GetAdminOfficeBranchesQuery : IRequest<Result<IReadOnlyList<AdminOfficeBranch>>>;

public sealed record AdminOfficeBranch(
    Guid Id,
    string Name,
    string Address,
    string State,
    string? City,
    string? Phone,
    string? OpeningHours,
    decimal Latitude,
    decimal Longitude,
    bool IsActive,
    int OpenRequests);

public sealed record SaveOfficeBranchCommand(
    Guid? Id,
    string Name,
    string Address,
    string State,
    string? City,
    string? Phone,
    string? OpeningHours,
    decimal Latitude,
    decimal Longitude,
    bool IsActive) : IRequest<Result<AdminOfficeBranch>>;

public sealed record GetOfficePickupCategoriesQuery : IRequest<Result<IReadOnlyList<OfficePickupCategory>>>;

/// <summary>Every category and subcategory with its office pickup switch. A subcategory is covered when it or its parent is on.</summary>
public sealed record OfficePickupCategory(Guid Id, string Name, Guid? ParentCategoryId, bool IsOfficePickupEligible);

public sealed record SetOfficePickupCategoryCommand(Guid CategoryId, bool Eligible) : IRequest<Result>;

public sealed class UpdateOfficePickupStatusCommandValidator : AbstractValidator<UpdateOfficePickupStatusCommand>
{
    public UpdateOfficePickupStatusCommandValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Note).MaximumLength(1000);
        RuleFor(x => x.DeliveryAddress).MaximumLength(500);
        When(x => x.Status == OfficePickupStatus.InspectionFailed, () =>
            RuleFor(x => x.Note).NotEmpty().WithMessage("Say why the item failed inspection - the buyer and seller see it."));
    }
}

public sealed class RefundOfficePickupCommandValidator : AbstractValidator<RefundOfficePickupCommand>
{
    public RefundOfficePickupCommandValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Give a reason for the refund.").MaximumLength(1000);
}

public sealed class SaveOfficeBranchCommandValidator : AbstractValidator<SaveOfficeBranchCommand>
{
    public SaveOfficeBranchCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Address).NotEmpty().MaximumLength(500);
        RuleFor(x => x.State).NotEmpty().MaximumLength(100);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.Phone).MaximumLength(20);
        RuleFor(x => x.OpeningHours).MaximumLength(200);
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
    }
}

public sealed class AdminOfficePickupHandlers :
    IRequestHandler<GetAdminOfficePickupsQuery, Result<AdminOfficePickupPage>>,
    IRequestHandler<GetAdminOfficePickupQuery, Result<AdminOfficePickupDetail>>,
    IRequestHandler<UpdateOfficePickupStatusCommand, Result>,
    IRequestHandler<RefundOfficePickupCommand, Result>,
    IRequestHandler<GetAdminOfficeBranchesQuery, Result<IReadOnlyList<AdminOfficeBranch>>>,
    IRequestHandler<SaveOfficeBranchCommand, Result<AdminOfficeBranch>>,
    IRequestHandler<GetOfficePickupCategoriesQuery, Result<IReadOnlyList<OfficePickupCategory>>>,
    IRequestHandler<SetOfficePickupCategoryCommand, Result>
{
    /// <summary>The pipeline staff move a paid request along. Refunds are a separate action.</summary>
    private static readonly Dictionary<OfficePickupStatus, OfficePickupStatus[]> Transitions = new()
    {
        [OfficePickupStatus.PaymentConfirmed] = [OfficePickupStatus.ItemReceived],
        [OfficePickupStatus.ItemReceived] = [OfficePickupStatus.InspectionPassed, OfficePickupStatus.InspectionFailed],
        [OfficePickupStatus.InspectionPassed] = [OfficePickupStatus.ReadyForCollection],
        [OfficePickupStatus.ReadyForCollection] = [OfficePickupStatus.OutForDelivery, OfficePickupStatus.Completed],
        [OfficePickupStatus.OutForDelivery] = [OfficePickupStatus.Completed]
    };

    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly OfficePickupNotifier _notifier;
    private readonly IAuditLogger _auditLogger;
    private readonly IDateTime _dateTime;
    private readonly ILogger<AdminOfficePickupHandlers> _logger;

    public AdminOfficePickupHandlers(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IPaymentGatewayResolver gatewayResolver,
        OfficePickupNotifier notifier, IAuditLogger auditLogger, IDateTime dateTime, ILogger<AdminOfficePickupHandlers> logger)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _gatewayResolver = gatewayResolver;
        _notifier = notifier;
        _auditLogger = auditLogger;
        _dateTime = dateTime;
        _logger = logger;
    }

    private Guid Me => _currentUser.UserId!.Value;

    public async Task<Result<AdminOfficePickupPage>> Handle(GetAdminOfficePickupsQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var query =
            from r in _dbContext.OfficePickupRequests
            join b in _dbContext.OfficeBranches on r.BranchId equals b.Id
            join buyer in _dbContext.Users on r.BuyerUserId equals buyer.Id
            join seller in _dbContext.Users on r.SellerUserId equals seller.Id
            select new { r, BranchName = b.Name, BuyerName = buyer.Name, SellerName = seller.Name };

        if (request.Status is { } status)
        {
            query = query.Where(x => x.r.Status == status);
        }
        else if (!request.IncludeUnpaid)
        {
            query = query.Where(x => x.r.Status != OfficePickupStatus.AwaitingPayment && x.r.Status != OfficePickupStatus.PaymentFailed
                                     && x.r.Status != OfficePickupStatus.PaymentCancelled);
        }

        if (request.BranchId is { } branchId)
        {
            query = query.Where(x => x.r.BranchId == branchId);
        }

        if (request.CancellationRequested)
        {
            query = query.Where(x => x.r.CancellationRequestedAt != null && x.r.Status != OfficePickupStatus.Refunded);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            query = query.Where(x =>
                x.r.Reference.ToLower().Contains(term)
                || x.r.ListingTitle.ToLower().Contains(term)
                || (x.BuyerName != null && x.BuyerName.ToLower().Contains(term))
                || (x.SellerName != null && x.SellerName.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.r.CreatedAt).ThenByDescending(x => x.r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(x => new AdminOfficePickupListItem(
            x.r.Id, x.r.Reference, x.r.ListingTitle, x.BuyerName, x.SellerName, x.BranchName, x.r.Status,
            OfficePickupRules.StatusLabel(x.r.Status), x.r.TotalKobo, x.r.CancellationRequestedAt is not null,
            x.r.CreatedAt, x.r.UpdatedAt)).ToList();

        return Result.Success(new AdminOfficePickupPage(items, page, pageSize, total));
    }

    public async Task<Result<AdminOfficePickupDetail>> Handle(GetAdminOfficePickupQuery request, CancellationToken cancellationToken)
    {
        var pickup = await _dbContext.OfficePickupRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RequestId, cancellationToken);
        if (pickup is null)
        {
            return Result.Failure<AdminOfficePickupDetail>(OfficePickupErrors.NotFound);
        }

        var branch = await _dbContext.OfficeBranches.AsNoTracking().FirstAsync(b => b.Id == pickup.BranchId, cancellationToken);
        var parties = await _dbContext.Users.AsNoTracking()
            .Where(u => u.Id == pickup.BuyerUserId || u.Id == pickup.SellerUserId)
            .Select(u => new AdminOfficePickupParty(u.Id, u.Name, u.Email, u.Phone))
            .ToListAsync(cancellationToken);
        var listing = await _dbContext.Listings.AsNoTracking()
            .Where(l => l.Id == pickup.ListingId)
            .Select(l => new { l.Status, l.MediaUrls })
            .FirstOrDefaultAsync(cancellationToken);

        var activities = await (
            from a in _dbContext.OfficePickupActivities.Where(a => a.RequestId == pickup.Id)
            join u in _dbContext.Users on a.ActorUserId equals u.Id into actorJoin
            from u in actorJoin.DefaultIfEmpty()
            orderby a.CreatedAt descending
            select new AdminOfficePickupActivity(a.Status, a.Title, a.Note, u == null ? null : u.Name, a.CreatedAt))
            .ToListAsync(cancellationToken);

        var fee = OfficePickupRules.CancellationFeeKobo(pickup.ItemPriceKobo);
        return Result.Success(new AdminOfficePickupDetail(
            pickup.Id,
            pickup.Reference,
            pickup.ListingId,
            pickup.ListingTitle,
            listing?.Status,
            listing?.MediaUrls.FirstOrDefault(),
            pickup.Status,
            OfficePickupRules.StatusLabel(pickup.Status),
            pickup.ItemPriceKobo,
            pickup.ServiceFeeKobo,
            pickup.TotalKobo,
            pickup.PaymentReference,
            pickup.PaidAt,
            UserOfficePickupHandlers.ToBranch(branch, null),
            parties.First(p => p.UserId == pickup.BuyerUserId),
            parties.First(p => p.UserId == pickup.SellerUserId),
            pickup.Fulfilment,
            pickup.DeliveryAddress,
            pickup.CancellationRequestedAt,
            pickup.CancellationReason,
            pickup.CancellationFeeKobo,
            pickup.RefundedKobo,
            pickup.RefundedAt,
            pickup.CompletedAt,
            pickup.CreatedAt,
            pickup.IsInProgress,
            pickup.TotalKobo - fee,
            fee,
            Transitions.GetValueOrDefault(pickup.Status, []),
            activities));
    }

    public async Task<Result> Handle(UpdateOfficePickupStatusCommand request, CancellationToken cancellationToken)
    {
        var pickup = await _dbContext.OfficePickupRequests.FirstOrDefaultAsync(r => r.Id == request.RequestId, cancellationToken);
        if (pickup is null)
        {
            return Result.Failure(OfficePickupErrors.NotFound);
        }

        if (!Transitions.GetValueOrDefault(pickup.Status, []).Contains(request.Status))
        {
            return Result.Failure(OfficePickupErrors.InvalidTransition);
        }

        var address = request.DeliveryAddress?.Trim();
        if (request.Status == OfficePickupStatus.OutForDelivery && string.IsNullOrEmpty(address))
        {
            return Result.Failure(OfficePickupErrors.DeliveryAddressRequired);
        }

        var now = _dateTime.UtcNow;
        var before = pickup.Status;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var branch = await _dbContext.OfficeBranches.AsNoTracking().FirstAsync(b => b.Id == pickup.BranchId, cancellationToken);

        if (request.Status == OfficePickupStatus.OutForDelivery)
        {
            pickup.SetFulfilment(OfficePickupFulfilment.HomeDelivery, address, now);
        }
        else if (request.Status == OfficePickupStatus.Completed && pickup.Fulfilment is null)
        {
            pickup.SetFulfilment(OfficePickupFulfilment.BranchCollection, null, now);
        }

        pickup.MoveTo(request.Status, now);
        _dbContext.OfficePickupActivities.Add(OfficePickupActivity.Create(
            pickup.Id, request.Status, OfficePickupRules.StatusLabel(request.Status), note, Me, now));
        await _auditLogger.LogAsync(Me, "OfficePickup.StatusChanged", nameof(OfficePickupRequest), pickup.Id,
            JsonSerializer.Serialize(new { Status = before.ToString() }),
            JsonSerializer.Serialize(new { Status = request.Status.ToString(), Note = note, DeliveryAddress = address }),
            cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(OfficePickupErrors.ConcurrentUpdate);
        }

        await NotifyStatusAsync(pickup, branch, note, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> Handle(RefundOfficePickupCommand request, CancellationToken cancellationToken)
    {
        var pickup = await _dbContext.OfficePickupRequests.FirstOrDefaultAsync(r => r.Id == request.RequestId, cancellationToken);
        if (pickup is null)
        {
            return Result.Failure(OfficePickupErrors.NotFound);
        }

        if (!pickup.IsInProgress || pickup.PaymentReference is not { } paymentReference)
        {
            return Result.Failure(OfficePickupErrors.NotRefundable);
        }

        var fee = OfficePickupRules.CancellationFeeKobo(pickup.ItemPriceKobo);
        var refundKobo = pickup.TotalKobo - fee;

        // Claim the request first (the xmin token stops two staff refunding it at once), then pay
        // out; if the gateway refuses, put it back exactly as it was.
        var before = pickup.Status;
        var now = _dateTime.UtcNow;
        pickup.MarkRefunded(fee, refundKobo, now);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(OfficePickupErrors.ConcurrentUpdate);
        }

        var refund = await _gatewayResolver.GetActive().ProcessPartialRefundAsync(paymentReference, refundKobo, cancellationToken);
        if (refund.IsFailure)
        {
            _logger.LogError("Gateway refused the refund for office pickup {Reference}", pickup.Reference);
            await _dbContext.OfficePickupRequests.Where(r => r.Id == pickup.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, before)
                .SetProperty(r => r.CancellationFeeKobo, (decimal?)null)
                .SetProperty(r => r.RefundedKobo, (decimal?)null)
                .SetProperty(r => r.RefundedAt, (DateTime?)null), cancellationToken);
            return Result.Failure(OfficePickupErrors.RefundFailed);
        }

        var reason = request.Reason.Trim();
        _dbContext.OfficePickupActivities.Add(OfficePickupActivity.Create(
            pickup.Id, OfficePickupStatus.Refunded, OfficePickupRules.StatusLabel(OfficePickupStatus.Refunded),
            $"{OfficePickupRules.FormatNaira(refundKobo)} refunded ({OfficePickupRules.FormatNaira(fee)} cancellation fee kept). {reason}",
            Me, now));
        await _auditLogger.LogAsync(Me, "OfficePickup.Refunded", nameof(OfficePickupRequest), pickup.Id,
            JsonSerializer.Serialize(new { Status = before.ToString() }),
            JsonSerializer.Serialize(new { RefundedKobo = refundKobo, CancellationFeeKobo = fee, Reason = reason }),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var branch = await _dbContext.OfficeBranches.AsNoTracking().FirstAsync(b => b.Id == pickup.BranchId, cancellationToken);
        await _notifier.NotifyAsync(pickup.BuyerUserId, pickup, $"Refund on its way - {pickup.Reference}",
            $"We've refunded {OfficePickupRules.FormatNaira(refundKobo)} to your payment method ({OfficePickupRules.FormatNaira(fee)} cancellation fee kept). It can take a few working days to show.",
            cancellationToken);
        var sellerHasDroppedOff = before is not OfficePickupStatus.PaymentConfirmed;
        await _notifier.NotifyAsync(pickup.SellerUserId, pickup, "Office pickup cancelled",
            sellerHasDroppedOff
                ? $"The office pickup for “{pickup.ListingTitle}” ({pickup.Reference}) was cancelled. Please collect your item from our {branch.Name} office."
                : $"The office pickup for “{pickup.ListingTitle}” ({pickup.Reference}) was cancelled - you don't need to drop the item off.",
            cancellationToken);

        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<AdminOfficeBranch>>> Handle(GetAdminOfficeBranchesQuery request, CancellationToken cancellationToken)
    {
        var openStatuses = new[]
        {
            OfficePickupStatus.PaymentConfirmed, OfficePickupStatus.ItemReceived, OfficePickupStatus.InspectionPassed,
            OfficePickupStatus.InspectionFailed, OfficePickupStatus.ReadyForCollection, OfficePickupStatus.OutForDelivery
        };
        var rows = await _dbContext.OfficeBranches.AsNoTracking()
            .OrderBy(b => b.State).ThenBy(b => b.Name)
            .Select(b => new AdminOfficeBranch(
                b.Id, b.Name, b.Address, b.State, b.City, b.Phone, b.OpeningHours, b.Latitude, b.Longitude, b.IsActive,
                _dbContext.OfficePickupRequests.Count(r => r.BranchId == b.Id && openStatuses.Contains(r.Status))))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<AdminOfficeBranch>>(rows);
    }

    public async Task<Result<AdminOfficeBranch>> Handle(SaveOfficeBranchCommand request, CancellationToken cancellationToken)
    {
        var now = _dateTime.UtcNow;
        string Clean(string value) => value.Trim();
        string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        OfficeBranch branch;
        if (request.Id is { } id)
        {
            var existing = await _dbContext.OfficeBranches.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
            if (existing is null)
            {
                return Result.Failure<AdminOfficeBranch>(OfficePickupErrors.BranchNotFound);
            }

            branch = existing;
            branch.Update(Clean(request.Name), Clean(request.Address), Clean(request.State), CleanOptional(request.City),
                CleanOptional(request.Phone), CleanOptional(request.OpeningHours), request.Latitude, request.Longitude, request.IsActive, now);
        }
        else
        {
            branch = OfficeBranch.Create(Clean(request.Name), Clean(request.Address), Clean(request.State), CleanOptional(request.City),
                CleanOptional(request.Phone), CleanOptional(request.OpeningHours), request.Latitude, request.Longitude, now);
            if (!request.IsActive)
            {
                branch.Update(branch.Name, branch.Address, branch.State, branch.City, branch.Phone, branch.OpeningHours,
                    branch.Latitude, branch.Longitude, false, now);
            }

            _dbContext.OfficeBranches.Add(branch);
        }

        await _auditLogger.LogAsync(Me, request.Id is null ? "OfficeBranch.Created" : "OfficeBranch.Updated", nameof(OfficeBranch),
            branch.Id, null, JsonSerializer.Serialize(new { branch.Name, branch.Address, branch.State, branch.IsActive }), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success((await Handle(new GetAdminOfficeBranchesQuery(), cancellationToken)).Value.Single(b => b.Id == branch.Id));
    }

    public async Task<Result<IReadOnlyList<OfficePickupCategory>>> Handle(GetOfficePickupCategoriesQuery request, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Categories.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new OfficePickupCategory(c.Id, c.Name, c.ParentCategoryId, c.IsOfficePickupEligible))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<OfficePickupCategory>>(rows);
    }

    public async Task<Result> Handle(SetOfficePickupCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = await _dbContext.Categories.FirstOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken);
        if (category is null)
        {
            return Result.Failure(new Error("Category.NotFound", "The category could not be found."));
        }

        var before = category.IsOfficePickupEligible;
        category.SetOfficePickupEligible(request.Eligible, _dateTime.UtcNow);
        await _auditLogger.LogAsync(Me, "Category.OfficePickupChanged", "Category", category.Id,
            JsonSerializer.Serialize(new { IsOfficePickupEligible = before }),
            JsonSerializer.Serialize(new { IsOfficePickupEligible = request.Eligible }), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task NotifyStatusAsync(OfficePickupRequest pickup, OfficeBranch branch, string? note, CancellationToken cancellationToken)
    {
        var title = pickup.ListingTitle;
        var withNote = note is null ? string.Empty : $" {note}";
        var (buyerTitle, buyerBody, sellerTitle, sellerBody) = pickup.Status switch
        {
            OfficePickupStatus.ItemReceived => (
                "Your item has arrived at our office",
                $"“{title}” was dropped at our {branch.Name} office. We're inspecting it now.",
                "Thanks - we've received your item",
                $"We've received “{title}” at our {branch.Name} office and will inspect it shortly."),
            OfficePickupStatus.InspectionPassed => (
                "Your item passed inspection",
                $"“{title}” passed our quality inspection.{withNote}",
                "Your item passed inspection",
                $"“{title}” passed our quality inspection.{withNote}"),
            OfficePickupStatus.InspectionFailed => (
                "Your item didn't pass inspection",
                $"“{title}” didn't pass our quality inspection.{withNote} We'll refund you, less the 1% cancellation fee.",
                "Your item didn't pass inspection",
                $"“{title}” didn't pass our quality inspection.{withNote} Please collect it from our {branch.Name} office."),
            OfficePickupStatus.ReadyForCollection => (
                "Your item is ready",
                $"Collect “{title}” at our {branch.Name} office, {branch.Address} - quote {pickup.Reference}. Prefer home delivery? Contact us and we'll arrange it.",
                (string?)null,
                (string?)null),
            OfficePickupStatus.OutForDelivery => (
                "Your item is on its way",
                $"“{title}” is out for delivery to {pickup.DeliveryAddress}.{withNote}",
                null,
                null),
            OfficePickupStatus.Completed => (
                "Office pickup complete",
                $"“{title}” has been handed over. Thanks for buying with Ogasela office pickup.",
                "Office pickup complete",
                $"The buyer has received “{title}” ({pickup.Reference})."),
            _ => ((string?)null, (string?)null, (string?)null, (string?)null)
        };

        if (buyerTitle is not null && buyerBody is not null)
        {
            await _notifier.NotifyAsync(pickup.BuyerUserId, pickup, buyerTitle, buyerBody, cancellationToken);
        }

        if (sellerTitle is not null && sellerBody is not null)
        {
            await _notifier.NotifyAsync(pickup.SellerUserId, pickup, sellerTitle, sellerBody, cancellationToken);
        }
    }
}

using System.Linq.Expressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.OfficePickups;
using Ogasela.Domain.Payments;
using Ogasela.Domain.Promotions;
using Ogasela.Shared;

namespace Ogasela.Application.Admin.Revenue;

/// <summary>
/// The Control Portal's Revenue page. Revenue is what Ogasela earns: successful promotion-plan purchases
/// (a refunded purchase is no longer Success, so it drops out) plus office pickup fees - the 3% service fee
/// on each paid pickup, or only the 1% cancellation fee once it's refunded, dated when the buyer paid. The
/// item price a buyer pays for a pickup belongs to the seller, so it's reported as collected, not earned.
/// Wallet top-ups are money collected from sellers that isn't earned until spent on a plan, so they're
/// reported separately, as is the total still sitting in wallets. Refunds cover both wallet refunds and
/// pickup refunds. From/To are UTC dates, both inclusive; the default is the last 30 days.
/// </summary>
public sealed record GetRevenueSummaryQuery(DateTime? From, DateTime? To) : IRequest<Result<RevenueSummary>>;

public sealed record RevenueSummary(
    DateTime From,
    DateTime To,
    /// <summary>"day" or "month" - month when the range is longer than ~3 months.</summary>
    string Bucket,
    decimal RevenueKobo,
    int PlanPurchases,
    decimal PreviousRevenueKobo,
    decimal TopUpsKobo,
    int TopUps,
    int PendingTopUps,
    int FailedTopUps,
    decimal RefundsKobo,
    int Refunds,
    int PayingSellers,
    decimal WalletBalancesKobo,
    decimal AllTimeRevenueKobo,
    IReadOnlyList<RevenuePoint> Series,
    IReadOnlyList<RevenueByPlan> ByPlan,
    IReadOnlyList<RevenueTopSeller> TopSellers,
    /// <summary>The plan-purchase part of RevenueKobo.</summary>
    decimal PlanRevenueKobo = 0,
    /// <summary>The office-pickup part of RevenueKobo: service fees, or the cancellation fee kept on a refund.</summary>
    decimal OfficePickupFeesKobo = 0,
    /// <summary>Pickups paid for in the period (refunded ones included - they still earned a cancellation fee).</summary>
    int OfficePickups = 0,
    /// <summary>Everything buyers paid for those pickups, item prices included - mostly owed to sellers.</summary>
    decimal OfficePickupCollectedKobo = 0);

/// <summary>RevenueKobo includes OfficePickupFeesKobo.</summary>
public sealed record RevenuePoint(DateTime Date, decimal RevenueKobo, decimal TopUpsKobo, decimal OfficePickupFeesKobo = 0);

public sealed record RevenueByPlan(string PlanName, decimal RevenueKobo, int Count);

public sealed record RevenueTopSeller(Guid UserId, string BusinessName, decimal RevenueKobo, int Count);

/// <summary>Every wallet transaction across all sellers, newest first. Search matches business name or gateway reference.</summary>
public sealed record GetRevenueTransactionsQuery(
    TransactionType? Type, TransactionStatus? Status, string? Search, DateTime? From, DateTime? To, int Page, int PageSize)
    : IRequest<Result<RevenueTransactionPage>>;

public sealed record RevenueTransactionItem(
    Guid Id,
    TransactionType Type,
    TransactionStatus Status,
    decimal AmountKobo,
    string? GatewayReference,
    Guid SellerUserId,
    string BusinessName,
    Guid? ListingId,
    string? ListingTitle,
    string? PlanName,
    DateTime CreatedAt);

public sealed record RevenueTransactionPage(IReadOnlyList<RevenueTransactionItem> Items, int Page, int PageSize, int TotalCount);

public sealed class AdminRevenueHandlers :
    IRequestHandler<GetRevenueSummaryQuery, Result<RevenueSummary>>,
    IRequestHandler<GetRevenueTransactionsQuery, Result<RevenueTransactionPage>>
{
    private const int MonthlyBucketThresholdDays = 92;

    /// <summary>What Ogasela keeps from a paid pickup: the cancellation fee if it was refunded, otherwise the service fee.</summary>
    private static readonly Expression<Func<OfficePickupRequest, decimal>> EarnedKobo = p =>
        p.Status == OfficePickupStatus.Refunded ? p.CancellationFeeKobo ?? 0m : p.ServiceFeeKobo;

    private readonly IApplicationDbContext _dbContext;
    private readonly IDateTime _dateTime;

    public AdminRevenueHandlers(IApplicationDbContext dbContext, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _dateTime = dateTime;
    }

    public async Task<Result<RevenueSummary>> Handle(GetRevenueSummaryQuery request, CancellationToken cancellationToken)
    {
        var (from, toExclusive) = Range(request.From, request.To);
        var length = toExclusive - from;
        var previousFrom = from - length;

        var rows = await _dbContext.Transactions
            .Where(t => t.CreatedAt >= from && t.CreatedAt < toExclusive)
            .Select(t => new { t.Type, t.Status, t.AmountKobo, t.CreatedAt, t.SellerId, t.RelatedListingId, t.PromotionPlanId })
            .ToListAsync(cancellationToken);

        var earned = rows.Where(r => r.Type == TransactionType.PlanPurchase && r.Status == TransactionStatus.Success).ToList();
        var topUps = rows.Where(r => r.Type == TransactionType.AdSpendTopUp).ToList();
        var paidTopUps = topUps.Where(r => r.Status is TransactionStatus.Success or TransactionStatus.Refunded).ToList();
        var refunds = rows.Where(r => r.Type == TransactionType.Refund && r.Status == TransactionStatus.Success).ToList();

        var pickups = await PaidPickups()
            .Where(p => p.PaidAt >= from && p.PaidAt < toExclusive)
            .Select(p => new
            {
                PaidAt = p.PaidAt!.Value,
                p.TotalKobo,
                EarnedKobo = p.Status == OfficePickupStatus.Refunded ? p.CancellationFeeKobo ?? 0m : p.ServiceFeeKobo,
            })
            .ToListAsync(cancellationToken);
        var pickupRefunds = await _dbContext.OfficePickupRequests
            .Where(p => p.Status == OfficePickupStatus.Refunded && p.RefundedAt >= from && p.RefundedAt < toExclusive)
            .Select(p => p.RefundedKobo ?? 0m)
            .ToListAsync(cancellationToken);

        var previousRevenue = (await _dbContext.Transactions
            .Where(t => t.Type == TransactionType.PlanPurchase && t.Status == TransactionStatus.Success
                        && t.CreatedAt >= previousFrom && t.CreatedAt < from)
            .SumAsync(t => (decimal?)t.AmountKobo, cancellationToken) ?? 0m)
            + (await PaidPickups()
                .Where(p => p.PaidAt >= previousFrom && p.PaidAt < from)
                .Select(EarnedKobo).SumAsync(fee => (decimal?)fee, cancellationToken) ?? 0m);
        var allTimeRevenue = (await _dbContext.Transactions
            .Where(t => t.Type == TransactionType.PlanPurchase && t.Status == TransactionStatus.Success)
            .SumAsync(t => (decimal?)t.AmountKobo, cancellationToken) ?? 0m)
            + (await PaidPickups().Select(EarnedKobo).SumAsync(fee => (decimal?)fee, cancellationToken) ?? 0m);
        var walletBalances = await _dbContext.Wallets.SumAsync(w => (decimal?)w.BalanceKobo, cancellationToken) ?? 0m;

        // Time series, with empty buckets filled in so the chart has no gaps.
        var monthly = length.TotalDays > MonthlyBucketThresholdDays;
        DateTime BucketOf(DateTime d) => monthly ? new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc) : d.Date;
        var series = new List<RevenuePoint>();
        for (var bucket = BucketOf(from); bucket < toExclusive; bucket = monthly ? bucket.AddMonths(1) : bucket.AddDays(1))
        {
            var start = bucket;
            var pickupFees = pickups.Where(p => BucketOf(p.PaidAt) == start).Sum(p => p.EarnedKobo);
            series.Add(new RevenuePoint(
                start,
                earned.Where(r => BucketOf(r.CreatedAt) == start).Sum(r => r.AmountKobo) + pickupFees,
                paidTopUps.Where(r => BucketOf(r.CreatedAt) == start).Sum(r => r.AmountKobo),
                pickupFees));
        }

        // By plan: the plan the payment bought. Older rows don't record it, so they fall back to the
        // plan now on the listing (which an upgrade since may have changed).
        var listingIds = earned.Where(r => r.PromotionPlanId == null && r.RelatedListingId != null)
            .Select(r => r.RelatedListingId!.Value).Distinct().ToList();
        var planByListing = await _dbContext.Listings
            .Where(l => listingIds.Contains(l.Id) && l.PromotionPlanId != null)
            .ToDictionaryAsync(l => l.Id, l => l.PromotionPlanId!.Value, cancellationToken);
        Guid? PlanOf(Guid? paymentPlanId, Guid? listingId) =>
            paymentPlanId ?? (listingId is { } id && planByListing.TryGetValue(id, out var planId) ? planId : null);
        var planIds = earned.Select(r => PlanOf(r.PromotionPlanId, r.RelatedListingId)).OfType<Guid>().Distinct().ToList();
        var planNames = await _dbContext.PromotionPlans
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var byPlan = earned
            .GroupBy(r => PlanOf(r.PromotionPlanId, r.RelatedListingId) is { } id && planNames.TryGetValue(id, out var name) ? name : "Other")
            .Select(g => new RevenueByPlan(g.Key, g.Sum(r => r.AmountKobo), g.Count()))
            .OrderByDescending(p => p.RevenueKobo)
            .ToList();

        var topSellerTotals = earned
            .GroupBy(r => r.SellerId)
            .Select(g => new { SellerId = g.Key, Total = g.Sum(r => r.AmountKobo), Count = g.Count() })
            .OrderByDescending(x => x.Total)
            .Take(5)
            .ToList();
        var topSellerIds = topSellerTotals.Select(x => x.SellerId).ToList();
        var sellers = await _dbContext.SellerProfiles
            .Where(s => topSellerIds.Contains(s.Id))
            .Select(s => new { s.Id, s.UserId, s.BusinessName })
            .ToDictionaryAsync(s => s.Id, cancellationToken);
        var topSellers = topSellerTotals
            .Where(x => sellers.ContainsKey(x.SellerId))
            .Select(x => new RevenueTopSeller(sellers[x.SellerId].UserId, sellers[x.SellerId].BusinessName, x.Total, x.Count))
            .ToList();

        var planRevenue = earned.Sum(r => r.AmountKobo);
        var pickupFeesTotal = pickups.Sum(p => p.EarnedKobo);

        return Result.Success(new RevenueSummary(
            from,
            toExclusive.AddDays(-1),
            monthly ? "month" : "day",
            planRevenue + pickupFeesTotal,
            earned.Count,
            previousRevenue,
            paidTopUps.Sum(r => r.AmountKobo),
            paidTopUps.Count,
            topUps.Count(r => r.Status == TransactionStatus.Pending),
            topUps.Count(r => r.Status == TransactionStatus.Failed),
            refunds.Sum(r => r.AmountKobo) + pickupRefunds.Sum(),
            refunds.Count + pickupRefunds.Count,
            earned.Select(r => r.SellerId).Distinct().Count(),
            walletBalances,
            allTimeRevenue,
            series,
            byPlan,
            topSellers,
            planRevenue,
            pickupFeesTotal,
            pickups.Count,
            pickups.Sum(p => p.TotalKobo)));
    }

    /// <summary>Pickups the buyer actually paid for - including ones refunded since.</summary>
    private IQueryable<OfficePickupRequest> PaidPickups() => _dbContext.OfficePickupRequests.Where(p => p.PaidAt != null);

    public async Task<Result<RevenueTransactionPage>> Handle(GetRevenueTransactionsQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var query =
            from t in _dbContext.Transactions
            join s in _dbContext.SellerProfiles on t.SellerId equals s.Id
            join l in _dbContext.Listings on t.RelatedListingId equals l.Id into listingJoin
            from l in listingJoin.DefaultIfEmpty()
            join p in _dbContext.PromotionPlans on t.PromotionPlanId ?? l.PromotionPlanId equals p.Id into planJoin
            from p in planJoin.DefaultIfEmpty()
            select new { t, s.UserId, s.BusinessName, ListingTitle = l.Title, Plan = p.Name };

        if (request.Type is { } type)
        {
            query = query.Where(x => x.t.Type == type);
        }

        if (request.Status is { } status)
        {
            query = query.Where(x => x.t.Status == status);
        }

        if (request.From is not null || request.To is not null)
        {
            var (from, toExclusive) = Range(request.From, request.To);
            query = query.Where(x => x.t.CreatedAt >= from && x.t.CreatedAt < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            query = query.Where(x =>
                x.BusinessName.ToLower().Contains(term)
                || (x.t.GatewayReference != null && x.t.GatewayReference.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.t.CreatedAt).ThenByDescending(x => x.t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(x => new RevenueTransactionItem(
                x.t.Id, x.t.Type, x.t.Status, x.t.AmountKobo, x.t.GatewayReference, x.UserId, x.BusinessName,
                x.t.RelatedListingId, x.ListingTitle, x.t.Type == TransactionType.PlanPurchase ? x.Plan : null, x.t.CreatedAt))
            .ToList();

        return Result.Success(new RevenueTransactionPage(items, page, pageSize, total));
    }

    private (DateTime From, DateTime ToExclusive) Range(DateTime? from, DateTime? to)
    {
        var today = _dateTime.UtcNow.Date;
        var end = DateTime.SpecifyKind((to ?? today).Date, DateTimeKind.Utc).AddDays(1);
        var start = DateTime.SpecifyKind((from ?? end.AddDays(-30)).Date, DateTimeKind.Utc);
        if (start >= end)
        {
            start = end.AddDays(-1);
        }

        return (start, end);
    }
}

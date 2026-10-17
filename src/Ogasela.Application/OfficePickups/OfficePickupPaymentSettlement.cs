using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Notifications;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Application.Payments;
using Ogasela.Domain.OfficePickups;

namespace Ogasela.Application.OfficePickups;

/// <summary>
/// Settles an office pickup payment once the gateway's verify call has answered. Shared by the
/// payment webhook and <see cref="ConfirmOfficePickupPaymentCommand"/> (webhooks can lag, and never
/// reach a local API), and idempotent: only an unpaid (AwaitingPayment or PaymentCancelled) request is touched, and its xmin
/// concurrency token makes a racing second settlement a no-op. On success the buyer gets an
/// emailed receipt and both parties are notified - the seller is told where to drop the item.
/// </summary>
public sealed class OfficePickupPaymentSettlement
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IPaymentGatewayResolver _gatewayResolver;
    private readonly OfficePickupNotifier _notifier;
    private readonly IEmailSender _emailSender;
    private readonly IDateTime _dateTime;
    private readonly ILogger<OfficePickupPaymentSettlement> _logger;

    public OfficePickupPaymentSettlement(
        IApplicationDbContext dbContext, IPaymentGatewayResolver gatewayResolver, OfficePickupNotifier notifier,
        IEmailSender emailSender, IDateTime dateTime, ILogger<OfficePickupPaymentSettlement> logger)
    {
        _dbContext = dbContext;
        _gatewayResolver = gatewayResolver;
        _notifier = notifier;
        _emailSender = emailSender;
        _dateTime = dateTime;
        _logger = logger;
    }

    public async Task SettleAsync(OfficePickupRequest request, PaymentVerificationResult verification, CancellationToken cancellationToken)
    {
        if (!request.IsAwaitingSettlement)
        {
            return;
        }

        // A buyer-cancelled checkout stays cancelled unless the money actually arrived.
        if (request.Status == OfficePickupStatus.PaymentCancelled && !verification.Successful)
        {
            return;
        }

        var now = _dateTime.UtcNow;
        var paid = verification.Successful && verification.AmountKobo >= request.TotalKobo;
        if (verification.Successful && !paid)
        {
            // Never keep money for a request we can't honour: give the underpayment straight back.
            _logger.LogWarning("Office pickup payment {Reference} was underpaid; refunding it", verification.Reference);
            var refund = await _gatewayResolver.GetActive().ProcessRefundAsync(verification.Reference, cancellationToken);
            if (refund.IsFailure)
            {
                _logger.LogError("Refunding underpaid office pickup payment {Reference} failed - refund it by hand", verification.Reference);
            }
        }

        if (paid)
        {
            request.MarkPaid(now);
            _dbContext.OfficePickupActivities.Add(OfficePickupActivity.Create(
                request.Id, OfficePickupStatus.PaymentConfirmed, OfficePickupRules.StatusLabel(OfficePickupStatus.PaymentConfirmed),
                $"We received your payment of {OfficePickupRules.FormatNaira(request.TotalKobo)}.", null, now));
        }
        else
        {
            request.MarkPaymentFailed(now);
            _dbContext.OfficePickupActivities.Add(OfficePickupActivity.Create(
                request.Id, OfficePickupStatus.PaymentFailed, OfficePickupRules.StatusLabel(OfficePickupStatus.PaymentFailed),
                "The payment didn't go through, so nothing was charged. You can try again from the ad.", null, now));
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent webhook/confirm settled the same payment first - nothing left to do.
            _logger.LogInformation("Concurrent settlement of office pickup {RequestId}; skipping", request.Id);
            return;
        }

        if (!paid)
        {
            return;
        }

        var branch = await _dbContext.OfficeBranches.AsNoTracking().FirstAsync(b => b.Id == request.BranchId, cancellationToken);
        await SendReceiptAsync(request, branch, cancellationToken);

        await _notifier.NotifyAsync(request.BuyerUserId, request, $"Payment confirmed - {request.Reference}",
            $"We've asked the seller to drop “{request.ListingTitle}” at our {branch.Name} office. We'll update you at every step.",
            cancellationToken);
        await _notifier.NotifyAsync(request.SellerUserId, request, "A buyer requested office pickup",
            $"A buyer has paid for “{request.ListingTitle}” through office pickup ({request.Reference}). Please drop the item at our {branch.Name} office, {branch.Address}.",
            cancellationToken);
    }

    private async Task SendReceiptAsync(OfficePickupRequest request, OfficeBranch branch, CancellationToken cancellationToken)
    {
        var buyer = await _dbContext.Users.AsNoTracking()
            .Where(u => u.Id == request.BuyerUserId)
            .Select(u => new { u.Email, u.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(buyer?.Email))
        {
            _logger.LogInformation("No email on file for the buyer of office pickup {RequestId}; skipping the receipt", request.Id);
            return;
        }

        var html = BrandedEmailTemplate.Render(
            previewText: $"Receipt for {request.Reference}: {OfficePickupRules.FormatNaira(request.TotalKobo)} paid",
            heading: "Your office pickup receipt",
            bodyParagraphs:
            [
                $"Hi {buyer.Name ?? "there"}, thanks for your payment. Here's your receipt.",
                $"Receipt number: {request.Reference}",
                $"Date paid: {request.PaidAt:d MMM yyyy, HH:mm} (UTC)",
                $"Item: {request.ListingTitle}",
                $"Item price: {OfficePickupRules.FormatNaira(request.ItemPriceKobo)}",
                $"Office pickup fee (3%): {OfficePickupRules.FormatNaira(request.ServiceFeeKobo)}",
                $"Total paid: {OfficePickupRules.FormatNaira(request.TotalKobo)}",
                $"Pickup branch: {branch.Name}, {branch.Address}",
                "What happens next: we'll ask the seller to drop the item at the branch, inspect it, then let you know when it's ready to collect (or deliver it if you prefer). You'll get an update in the app at every step.",
                "If the item fails inspection, or you decide not to go ahead, you'll be refunded less a 1% cancellation fee, as set out in our Terms of Service."
            ],
            eyebrow: "Receipt");

        try
        {
            await _emailSender.SendAsync(
                new EmailMessage(buyer.Email, buyer.Name, $"Receipt for your office pickup {request.Reference}", html), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to email the receipt for office pickup {RequestId}", request.Id);
        }
    }
}

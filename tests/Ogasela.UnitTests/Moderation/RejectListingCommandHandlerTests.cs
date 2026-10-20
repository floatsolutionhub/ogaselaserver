using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Moderation.ListingReview;
using Ogasela.Application.Payments;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Payments;
using Ogasela.Shared;
using Ogasela.UnitTests.TestSupport;

namespace Ogasela.UnitTests.Moderation;

public class RejectListingCommandHandlerTests
{
    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public Task LogAsync(Guid actorId, string action, string targetType, Guid targetId,
            string? beforeJson, string? afterJson, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Records full refunds; set <see cref="Refuse"/> to make the gateway fail them.</summary>
    private sealed class RefundingGateway : IPaymentGateway, IPaymentGatewayResolver
    {
        public bool Refuse { get; init; }
        public List<string> Refunded { get; } = [];
        public string ProviderName => "Fake";

        public Task<Result> ProcessRefundAsync(string transactionReference, CancellationToken cancellationToken)
        {
            if (Refuse)
            {
                return Task.FromResult(Result.Failure(PaymentErrors.GatewayError));
            }

            Refunded.Add(transactionReference);
            return Task.FromResult(Result.Success());
        }

        public IPaymentGateway GetActive() => this;
        public IPaymentGateway? GetByProviderName(string providerName) => this;

        public Task<Result<PaymentInitialization>> InitializeTransactionAsync(Guid sellerId, decimal amountKobo, string purpose, string? callbackUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<PaymentInitialization>> InitializeCustomerTransactionAsync(string customerEmail, decimal amountKobo, string purpose, string? callbackUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<PaymentVerificationResult>> VerifyTransactionAsync(string reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> ProcessPartialRefundAsync(string transactionReference, decimal amountKobo, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool VerifyWebhookSignature(string rawBody, string? signatureHeaderValue) => throw new NotSupportedException();
        public string? ExtractReferenceFromWebhookPayload(string rawBody) => throw new NotSupportedException();
    }

    private static RejectListingCommandHandler NewHandler(TestApplicationDbContext db, FakeDateTime dateTime, RefundingGateway gateway) =>
        new(db, new FakeCurrentUserService { UserId = Guid.NewGuid() }, new NoOpAuditLogger(), new FakePublisher(), dateTime,
            new PlanChargeRefunder(db, gateway, dateTime, NullLogger<PlanChargeRefunder>.Instance));

    private static async Task<(TestApplicationDbContext Db, FakeDateTime DateTime, Listing Listing, Transaction Charge)> SetupAsync(
        string? gatewayReference)
    {
        var db = TestApplicationDbContext.Create();
        var dateTime = new FakeDateTime();
        var sellerId = Guid.NewGuid();

        var listing = Listing.CreateDraft(
            Guid.NewGuid(), sellerId, Guid.NewGuid(), Guid.NewGuid(), "Laptop", "Barely used laptop for sale",
            250_000m, ListingCondition.Used, ["https://example.com/a.jpg"], dateTime.UtcNow);
        listing.SubmitForReview(dateTime.UtcNow);
        db.Listings.Add(listing);
        db.Wallets.Add(Wallet.Create(sellerId, dateTime.UtcNow));

        Transaction charge;
        if (gatewayReference is null)
        {
            charge = Transaction.CreateSucceeded(sellerId, TransactionType.PlanPurchase, 7_500_000m, null, listing.Id, dateTime.UtcNow);
        }
        else
        {
            charge = Transaction.CreatePending(sellerId, TransactionType.PlanPurchase, 7_500_000m, gatewayReference, dateTime.UtcNow, listing.Id);
            charge.MarkSuccess();
        }

        db.Transactions.Add(charge);
        await db.SaveChangesAsync(CancellationToken.None);
        return (db, dateTime, listing, charge);
    }

    [Fact]
    public async Task Handle_CardPaidListing_GoesBackToDraftAndRefundsTheCardNotTheWallet()
    {
        var (db, dateTime, listing, charge) = await SetupAsync("PSK-123");
        var gateway = new RefundingGateway();

        var result = await NewHandler(db, dateTime, gateway).Handle(new RejectListingCommand(listing.Id, "Wrong category"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.Listings.SingleAsync(l => l.Id == listing.Id);
        reloaded.Status.Should().Be(ListingStatus.Draft);
        reloaded.ReviewNote.Should().Be("Wrong category");

        gateway.Refunded.Should().Equal("PSK-123");
        (await db.Wallets.SingleAsync()).BalanceKobo.Should().Be(0m);
        (await db.Transactions.SingleAsync(t => t.Id == charge.Id)).Status.Should().Be(TransactionStatus.Refunded);
        (await db.Transactions.CountAsync(t => t.Type == TransactionType.Refund)).Should().Be(1);
    }

    [Fact]
    public async Task Handle_GatewayRefusesTheRefund_LeavesTheListingInReviewAndTheChargeIntact()
    {
        var (db, dateTime, listing, charge) = await SetupAsync("PSK-123");

        var result = await NewHandler(db, dateTime, new RefundingGateway { Refuse = true })
            .Handle(new RejectListingCommand(listing.Id, "Wrong category"), CancellationToken.None);

        result.Error.Should().Be(PaymentErrors.RefundFailed);
        (await db.Listings.SingleAsync(l => l.Id == listing.Id)).Status.Should().Be(ListingStatus.PendingReview);
        (await db.Transactions.SingleAsync(t => t.Id == charge.Id)).Status.Should().Be(TransactionStatus.Success);
        (await db.Transactions.AnyAsync(t => t.Type == TransactionType.Refund)).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WalletPaidListingFromBeforeTheWalletWasSwitchedOff_IsRefundedToTheWallet()
    {
        var (db, dateTime, listing, charge) = await SetupAsync(null);
        var gateway = new RefundingGateway();

        var result = await NewHandler(db, dateTime, gateway).Handle(new RejectListingCommand(listing.Id, "Wrong category"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        gateway.Refunded.Should().BeEmpty();
        (await db.Wallets.SingleAsync()).BalanceKobo.Should().Be(7_500_000m);
        (await db.Transactions.SingleAsync(t => t.Id == charge.Id)).Status.Should().Be(TransactionStatus.Refunded);
    }

    [Fact]
    public async Task Handle_ListingNotPending_IsRejected()
    {
        var db = TestApplicationDbContext.Create();
        var dateTime = new FakeDateTime();
        var listing = Listing.CreateDraft(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Chair", "A sturdy wooden chair",
            10_000m, ListingCondition.New, [], dateTime.UtcNow);
        db.Listings.Add(listing);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await NewHandler(db, dateTime, new RefundingGateway())
            .Handle(new RejectListingCommand(listing.Id, "Spam"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Moderation.ListingNotPendingReview");
    }
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Payments;
using Ogasela.Shared;

namespace Ogasela.Application.Payments.FundWallet;

public sealed class FundWalletCommandHandler : IRequestHandler<FundWalletCommand, Result<FundWalletResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;
    private readonly IPaymentGatewayResolver _gatewayResolver;

    public FundWalletCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime,
        IPaymentGatewayResolver gatewayResolver)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
        _gatewayResolver = gatewayResolver;
    }

    public async Task<Result<FundWalletResponse>> Handle(FundWalletCommand request, CancellationToken cancellationToken)
    {
        var sellerId = await _dbContext.SellerProfiles
            .Where(s => s.UserId == _currentUser.UserId!.Value)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (sellerId == Guid.Empty)
        {
            return Result.Failure<FundWalletResponse>(PaymentErrors.SellerProfileNotFound);
        }

        var gateway = _gatewayResolver.GetActive();

        var initResult = await gateway.InitializeTransactionAsync(
            sellerId, request.AmountKobo, "Ogasela wallet funding", null, cancellationToken);

        if (initResult.IsFailure)
        {
            return Result.Failure<FundWalletResponse>(PaymentErrors.GatewayError);
        }

        var transaction = Transaction.CreatePending(
            sellerId, TransactionType.AdSpendTopUp, request.AmountKobo, initResult.Value.Reference, _dateTime.UtcNow);

        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new FundWalletResponse(
            transaction.Id, initResult.Value.Reference, initResult.Value.RedirectUrl));
    }
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Admin.EraseUserData;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.Listings;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.DeleteMyAccount;

/// <summary>
/// Takes the user's ads off the marketplace, then runs the same NDPA erasure staff use
/// (EraseUserDataCommand) - PII anonymized, sign-ins removed, sessions revoked. Transactions,
/// reviews and audit history are kept anonymized. The erasure's SaveChanges also persists the
/// takedowns, so it all lands together.
/// </summary>
public sealed class DeleteMyAccountCommandHandler : IRequestHandler<DeleteMyAccountCommand, Result>
{
    public const string TakedownReason = "The seller deleted their account.";

    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTime _dateTime;
    private readonly ISender _sender;

    public DeleteMyAccountCommandHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTime dateTime, ISender sender)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _dateTime = dateTime;
        _sender = sender;
    }

    public async Task<Result> Handle(DeleteMyAccountCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId!.Value;

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AccountErrors.UserNotFound);
        }

        // Staff accounts are offboarded through the Control Portal, not the customer app.
        if (user.Role is not (UserRole.Buyer or UserRole.Seller))
        {
            return Result.Failure(AccountErrors.StaffCannotSelfDelete);
        }

        var now = _dateTime.UtcNow;
        var listings = await (
            from l in _dbContext.Listings
            join s in _dbContext.SellerProfiles on l.SellerId equals s.Id
            where s.UserId == userId
                  && l.Status != ListingStatus.Sold && l.Status != ListingStatus.Expired && l.Status != ListingStatus.Paused
                  && l.Status != ListingStatus.Deleted
            select l)
            .ToListAsync(cancellationToken);

        foreach (var listing in listings)
        {
            listing.TakeDown(TakedownReason, now);
        }

        var erased = await _sender.Send(new EraseUserDataCommand(userId), cancellationToken);
        return erased.IsSuccess ? Result.Success() : Result.Failure(erased.Error);
    }
}

using Ogasela.Application.Accounts.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.GetCurrentUser;

public sealed class GetCurrentUserQueryHandler : IRequestHandler<GetCurrentUserQuery, Result<CurrentUserResponse>>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUserService;
    private readonly IProfilePhotoStorage _photoStorage;

    public GetCurrentUserQueryHandler(
        IApplicationDbContext dbContext, ICurrentUserService currentUserService, IProfilePhotoStorage photoStorage)
    {
        _photoStorage = photoStorage;
        _dbContext = dbContext;
        _currentUserService = currentUserService;
    }

    public async Task<Result<CurrentUserResponse>> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUserService.UserId;
        if (userId is null)
        {
            return Result.Failure<CurrentUserResponse>(AccountErrors.UserNotFound);
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<CurrentUserResponse>(AccountErrors.UserNotFound);
        }

        SellerProfileResponse? sellerProfileResponse = null;
        if (user.Role == UserRole.Seller)
        {
            var sellerProfile = await _dbContext.SellerProfiles
                .FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

            if (sellerProfile is not null)
            {
                sellerProfileResponse = new SellerProfileResponse(
                    sellerProfile.Id,
                    sellerProfile.BusinessName,
                    sellerProfile.RcNumber,
                    sellerProfile.Nin,
                    sellerProfile.VerificationStatus,
                    sellerProfile.VerifiedAt,
                    sellerProfile.StoreAddress,
                    sellerProfile.BusinessVerifiedAt);
            }
        }

        StaffProfileResponse? staffProfileResponse = null;
        if (user.Role == UserRole.Staff)
        {
            var staffProfile = await _dbContext.StaffProfiles
                .FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

            if (staffProfile is not null)
            {
                var department = await _dbContext.Departments
                    .FirstOrDefaultAsync(d => d.Id == staffProfile.DepartmentId, cancellationToken);
                var unit = await _dbContext.Units
                    .FirstOrDefaultAsync(u => u.Id == staffProfile.UnitId, cancellationToken);
                var role = await _dbContext.Roles
                    .FirstOrDefaultAsync(r => r.Id == staffProfile.RoleId, cancellationToken);

                if (department is not null && unit is not null && role is not null)
                {
                    staffProfileResponse = new StaffProfileResponse(
                        staffProfile.Id,
                        department.Name,
                        unit.Name,
                        role.Name,
                        role.RoleType,
                        role.PermissionKeys,
                        staffProfile.Status);
                }
            }
        }

        var response = new CurrentUserResponse(
            user.Id,
            user.Name,
            user.Phone,
            user.Email,
            user.Role,
            user.CreatedAt,
            user.MustChangePassword,
            sellerProfileResponse,
            staffProfileResponse,
            user.ProfilePhotoS3Key is null ? null : _photoStorage.GetImageUrl(user.ProfilePhotoS3Key));

        return Result.Success(response);
    }
}

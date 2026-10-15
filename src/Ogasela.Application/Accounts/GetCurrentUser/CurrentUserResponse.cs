using Ogasela.Domain.Accounts;
using Ogasela.Domain.Rbac;
using Ogasela.Domain.Staff;

namespace Ogasela.Application.Accounts.GetCurrentUser;

public sealed record CurrentUserResponse(
    Guid Id,
    string? Name,
    string? Phone,
    string? Email,
    UserRole Role,
    DateTime CreatedAt,
    bool MustChangePassword,
    SellerProfileResponse? SellerProfile,
    StaffProfileResponse? StaffProfile,
    string? ProfilePhotoUrl = null);

public sealed record SellerProfileResponse(
    Guid Id,
    string BusinessName,
    string? RcNumber,
    string? Nin,
    VerificationStatus VerificationStatus,
    DateTime? VerifiedAt,
    string? StoreAddress = null,
    DateTime? BusinessVerifiedAt = null);

/// <summary>
/// Lets the frontend gate routes by permission/roleType and show a force-password-change screen
/// without a second API call - Permissions is the flattened PermissionKeys list off the staff
/// member's assigned Role.
/// </summary>
public sealed record StaffProfileResponse(
    Guid Id,
    string DepartmentName,
    string UnitName,
    string RoleName,
    RoleType RoleType,
    IReadOnlyList<string> Permissions,
    StaffOnboardingStatus Status);

namespace Ogasela.Domain.Accounts;

public class User
{
    private User()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Optional - not collected by public self-registration (Buyer/Seller), but useful for internal-role accounts (Moderator/FinanceAdmin/SuperAdmin), where an admin portal wants a real name to display rather than a phone number.</summary>
    public string? Name { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string PasswordHash { get; private set; } = string.Empty;

    public UserRole Role { get; private set; }

    /// <summary>FCM device registration token, if the user has ever registered one. Null means push is a no-op for them.</summary>
    public string? PushToken { get; private set; }

    /// <summary>Private-storage key of the profile photo (taken with the app's face capture), if any.</summary>
    public string? ProfilePhotoS3Key { get; private set; }

    /// <summary>Set while an admin has suspended the account: it can't sign in by any route until reactivated.</summary>
    public DateTime? SuspendedAt { get; private set; }

    public string? SuspensionReason { get; private set; }

    public bool IsSuspended => SuspendedAt is not null;

    /// <summary>Set true when a staff account's password was just generated for them (onboarding approval) and emailed in plaintext - forces a rotation before anything else, cleared by ChangePasswordCommand.</summary>
    public bool MustChangePassword { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>When the user proved they own Email (a code sent there, or a provider that vouches for it). Null = unverified.</summary>
    public DateTime? EmailVerifiedAt { get; private set; }

    /// <summary>When the user proved they own Phone (a code sent there by SMS). Null = unverified.</summary>
    public DateTime? PhoneVerifiedAt { get; private set; }

    /// <summary>When the account was deleted (by the user, or erased by staff). Its chats stay readable but closed.</summary>
    public DateTime? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public bool IsEmailVerified => Email is not null && EmailVerifiedAt is not null;

    public bool IsPhoneVerified => Phone is not null && PhoneVerifiedAt is not null;

    public void MarkEmailVerified(DateTime now) => EmailVerifiedAt ??= now;

    public void MarkPhoneVerified(DateTime now) => PhoneVerifiedAt ??= now;

    public static User Create(string? phone, string? email, string passwordHash, UserRole role, string? name = null)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Name = name,
            Phone = phone,
            Email = email,
            PasswordHash = passwordHash,
            Role = role,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <param name="verifiedAt">Set when the new value was proven by a code sent to it; otherwise the changed value starts unverified.</param>
    public void UpdateContactDetails(string? phone, string? email, DateTime? verifiedAt = null)
    {
        if (phone is not null && phone != Phone)
        {
            Phone = phone;
            PhoneVerifiedAt = verifiedAt;
        }

        if (email is not null && email != Email)
        {
            Email = email;
            EmailVerifiedAt = verifiedAt;
        }
    }

    public void SetPushToken(string? pushToken) => PushToken = pushToken;

    public void UpdateName(string name) => Name = name.Trim();

    public void SetProfilePhoto(string? s3Key) => ProfilePhotoS3Key = s3Key;

    public void Suspend(string reason, DateTime now)
    {
        SuspendedAt = now;
        SuspensionReason = reason;
    }

    public void Reactivate()
    {
        SuspendedAt = null;
        SuspensionReason = null;
    }

    /// <summary>Generic password-hash mutator. Used by ChangePasswordCommand and by staff-onboarding approval (which generates and hashes a temp password for a newly-approved staff account).</summary>
    public void SetPasswordHash(string passwordHash, bool mustChangePassword = false)
    {
        PasswordHash = passwordHash;
        MustChangePassword = mustChangePassword;
    }

    /// <summary>A buyer becomes a seller once staff verify their identity. No-op for any other role.</summary>
    public void BecomeSeller()
    {
        if (Role == UserRole.Buyer)
        {
            Role = UserRole.Seller;
        }
    }

    /// <summary>
    /// Promotes an existing account to SuperAdmin in place. Used only by SuperAdminSeeder for
    /// the case where its configured seed phone number already belongs to an account (typically
    /// the intended admin's own prior Buyer/Seller signup) - avoids either failing on the Phone
    /// unique constraint or silently skipping the seed.
    /// </summary>
    public void PromoteToSuperAdmin(string? name, string? email, string passwordHash)
    {
        Role = UserRole.SuperAdmin;
        PasswordHash = passwordHash;

        if (name is not null)
        {
            Name = name;
        }

        if (email is not null)
        {
            Email = email;
        }
    }

    /// <summary>
    /// NDPA data-subject deletion: clears every directly-identifying field. PasswordHash is
    /// replaced (rather than nulled) with a value no plaintext password can ever hash to, so the
    /// account is permanently unable to log in - Id/Role/CreatedAt are kept, since Transactions,
    /// AuditLogs, etc. still reference this UserId for financial/audit history. DeletedAt keeps the
    /// first deletion time if it's erased again.
    /// </summary>
    public void Anonymize(string unusablePasswordHash, DateTime now)
    {
        DeletedAt ??= now;
        Name = null;
        Phone = null;
        Email = null;
        PushToken = null;
        ProfilePhotoS3Key = null;
        PasswordHash = unusablePasswordHash;
    }
}

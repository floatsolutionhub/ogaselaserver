using Microsoft.EntityFrameworkCore;
using Ogasela.Domain.Accounts;
using Ogasela.Domain.Analytics;
using Ogasela.Domain.AdIntegrations;
using Ogasela.Domain.Geo;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Marketing;
using Ogasela.Domain.Messaging;
using Ogasela.Domain.Moderation;
using Ogasela.Domain.Notifications;
using Ogasela.Domain.OfficePickups;
using Ogasela.Domain.Payments;
using Ogasela.Domain.Promotions;
using Ogasela.Domain.Rbac;
using Ogasela.Domain.Reviews;
using Ogasela.Domain.Shared;
using Ogasela.Domain.Staff;
using Ogasela.Domain.Verification;

namespace Ogasela.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }

    DbSet<SellerProfile> SellerProfiles { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<ExternalLogin> ExternalLogins { get; }

    DbSet<ListingDailyStat> ListingDailyStats { get; }

    DbSet<SellerDailyStat> SellerDailyStats { get; }

    DbSet<Announcement> Announcements { get; }

    DbSet<InboxNotification> InboxNotifications { get; }

    DbSet<UserDocument> UserDocuments { get; }

    DbSet<ProfileChangeRequest> ProfileChangeRequests { get; }

    DbSet<Dispute> Disputes { get; }

    DbSet<OfficeBranch> OfficeBranches { get; }

    DbSet<OfficePickupRequest> OfficePickupRequests { get; }

    DbSet<OfficePickupActivity> OfficePickupActivities { get; }

    DbSet<Ogasela.Domain.Settings.PlatformSetting> PlatformSettings { get; }

    DbSet<DisputeMessage> DisputeMessages { get; }

    DbSet<BiometricVerification> BiometricVerifications { get; }

    DbSet<BiometricConsent> BiometricConsents { get; }

    DbSet<VerificationAuditEntry> VerificationAuditEntries { get; }

    DbSet<Category> Categories { get; }

    DbSet<PromotionPlan> PromotionPlans { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<Listing> Listings { get; }

    DbSet<Wallet> Wallets { get; }

    DbSet<Transaction> Transactions { get; }

    DbSet<Conversation> Conversations { get; }

    DbSet<Message> Messages { get; }

    DbSet<BlockedUser> BlockedUsers { get; }

    DbSet<Report> Reports { get; }

    DbSet<Review> Reviews { get; }

    DbSet<TrustScore> TrustScores { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<NotificationPreference> NotificationPreferences { get; }

    DbSet<AdAccountConnection> AdAccountConnections { get; }

    DbSet<AdCampaign> AdCampaigns { get; }

    DbSet<Department> Departments { get; }

    DbSet<OrgUnit> Units { get; }

    DbSet<Permission> Permissions { get; }

    DbSet<Role> Roles { get; }

    DbSet<StaffProfile> StaffProfiles { get; }

    DbSet<StaffKycDocument> StaffKycDocuments { get; }

    DbSet<NigeriaState> NigeriaStates { get; }

    DbSet<NigeriaCity> NigeriaCities { get; }

    DbSet<ContactFormSubmission> ContactFormSubmissions { get; }

    DbSet<InterestSignup> InterestSignups { get; }

    DbSet<Testimonial> Testimonials { get; }

    DbSet<AccountDeletionRequest> AccountDeletionRequests { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="operation"/> inside an explicit database transaction (via the
    /// provider's execution strategy, so it composes with any configured retry-on-failure).
    /// Needed wherever a unit of work crosses more than one <c>SaveChangesAsync</c> call, or
    /// must be safely re-run - e.g. verifying a webhook is not guaranteed exactly-once.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken);
}

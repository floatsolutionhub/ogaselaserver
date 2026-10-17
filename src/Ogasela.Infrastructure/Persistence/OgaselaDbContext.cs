using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
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

namespace Ogasela.Infrastructure.Persistence;

public class OgaselaDbContext : DbContext, IApplicationDbContext
{
    public OgaselaDbContext(DbContextOptions<OgaselaDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<SellerProfile> SellerProfiles => Set<SellerProfile>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();

    public DbSet<ListingDailyStat> ListingDailyStats => Set<ListingDailyStat>();

    public DbSet<SellerDailyStat> SellerDailyStats => Set<SellerDailyStat>();

    public DbSet<Announcement> Announcements => Set<Announcement>();

    public DbSet<InboxNotification> InboxNotifications => Set<InboxNotification>();

    public DbSet<UserDocument> UserDocuments => Set<UserDocument>();

    public DbSet<ProfileChangeRequest> ProfileChangeRequests => Set<ProfileChangeRequest>();

    public DbSet<Dispute> Disputes => Set<Dispute>();

    public DbSet<Ogasela.Domain.Settings.PlatformSetting> PlatformSettings => Set<Ogasela.Domain.Settings.PlatformSetting>();

    public DbSet<DisputeMessage> DisputeMessages => Set<DisputeMessage>();

    public DbSet<OfficeBranch> OfficeBranches => Set<OfficeBranch>();

    public DbSet<OfficePickupRequest> OfficePickupRequests => Set<OfficePickupRequest>();

    public DbSet<OfficePickupActivity> OfficePickupActivities => Set<OfficePickupActivity>();

    public DbSet<BiometricVerification> BiometricVerifications => Set<BiometricVerification>();

    public DbSet<BiometricConsent> BiometricConsents => Set<BiometricConsent>();

    public DbSet<VerificationAuditEntry> VerificationAuditEntries => Set<VerificationAuditEntry>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<PromotionPlan> PromotionPlans => Set<PromotionPlan>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Listing> Listings => Set<Listing>();

    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<BlockedUser> BlockedUsers => Set<BlockedUser>();

    public DbSet<Report> Reports => Set<Report>();

    public DbSet<Review> Reviews => Set<Review>();

    public DbSet<TrustScore> TrustScores => Set<TrustScore>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<AdAccountConnection> AdAccountConnections => Set<AdAccountConnection>();

    public DbSet<AdCampaign> AdCampaigns => Set<AdCampaign>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<OrgUnit> Units => Set<OrgUnit>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<StaffProfile> StaffProfiles => Set<StaffProfile>();

    public DbSet<StaffKycDocument> StaffKycDocuments => Set<StaffKycDocument>();

    public DbSet<NigeriaState> NigeriaStates => Set<NigeriaState>();

    public DbSet<NigeriaCity> NigeriaCities => Set<NigeriaCity>();

    public DbSet<ContactFormSubmission> ContactFormSubmissions => Set<ContactFormSubmission>();

    public DbSet<TesterSignup> TesterSignups => Set<TesterSignup>();

    public DbSet<AccountDeletionRequest> AccountDeletionRequests => Set<AccountDeletionRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }

    public async Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        var strategy = Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }
}

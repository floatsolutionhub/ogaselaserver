using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Rbac;

namespace Ogasela.Infrastructure.Rbac;

/// <summary>
/// Seeds the fixed Permission catalog every Role's PermissionKeys is validated against, plus
/// three baseline Role rows (Super Admin/Admin/HR) so the staff-creation hierarchy is usable out
/// of the box without a SuperAdmin having to hand-build those role shells first via the UI first.
/// Idempotent (upsert-by-Key for permissions, upsert-by-Name for the baseline roles) and safe to
/// run on every startup, same as SuperAdminSeeder.
/// </summary>
public static class RbacSeeder
{
    private static readonly (string Key, string Category, string Label)[] Catalog =
    [
        ("rbac.department.manage", "Rbac", "Manage departments"),
        ("rbac.unit.manage", "Rbac", "Manage units"),
        ("rbac.role.manage", "Rbac", "Manage roles"),
        ("staff.view", "Staff", "View staff"),
        ("staff.create", "Staff", "Onboard staff"),
        ("staff.kyc.review", "Staff", "Review staff KYC documents"),
        ("staff.kyc.approve", "Staff", "Approve staff onboarding"),
        ("staff.kyc.reject", "Staff", "Reject staff onboarding"),
        ("geo.state.manage", "Geo", "Manage Nigeria states"),
        ("geo.city.manage", "Geo", "Manage Nigeria cities"),
        ("moderation.queue.view", "Moderation", "View moderation queue"),
        ("moderation.report.resolve", "Moderation", "Resolve reports"),
        ("verification.queue.view", "Verification", "View manual review queue"),
        ("verification.decision.make", "Verification", "Decide manual review"),
        ("finance.dashboard.view", "Finance", "View platform dashboard"),
        ("audit.log.view", "Audit", "View audit log"),
        ("dataprivacy.erase", "DataPrivacy", "Erase user data"),
        ("users.view", "Users", "View app users"),
        ("users.manage", "Users", "Suspend, reactivate and sign out app users"),
        ("users.documents.review", "Users", "Review user ID and address documents"),
        ("users.changes.review", "Users", "Approve or reject phone, email and business info changes"),
        ("users.verify", "Users", "Verify sellers (or revoke verification) after reviewing their documents"),
        ("listings.view", "Listings", "View all ads"),
        ("listings.manage", "Listings", "Take down, reinstate and recategorise ads"),
        ("disputes.view", "Disputes", "View buyer-seller disputes"),
        ("disputes.manage", "Disputes", "Work and resolve disputes"),
        ("officepickup.view", "OfficePickup", "View office pickup requests"),
        ("officepickup.manage", "OfficePickup", "Update office pickup progress (item received, inspection, ready, delivered)"),
        ("officepickup.refund", "OfficePickup", "Refund office pickup requests"),
        ("officepickup.setup", "OfficePickup", "Manage office pickup branches and eligible categories"),
        ("marketing.interest.view", "Marketing", "View waiting-list and tester signups"),
        ("marketing.interest.manage", "Marketing", "Update, export and delete waiting-list and tester signups")
    ];

    public static async Task SeedAsync(IApplicationDbContext dbContext, CancellationToken cancellationToken)
    {
        var existingKeys = await dbContext.Permissions.Select(p => p.Key).ToListAsync(cancellationToken);
        foreach (var (key, category, label) in Catalog)
        {
            if (!existingKeys.Contains(key))
            {
                dbContext.Permissions.Add(Permission.Create(key, category, label));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var allKeys = Catalog.Select(p => p.Key).ToArray();
        var adminKeys = new[]
        {
            "rbac.department.manage", "rbac.unit.manage", "rbac.role.manage",
            "staff.view", "staff.create", "staff.kyc.review", "staff.kyc.approve", "staff.kyc.reject",
            "geo.state.manage", "geo.city.manage",
            "finance.dashboard.view", "audit.log.view", "moderation.queue.view", "verification.queue.view",
            "users.view", "listings.view", "disputes.view", "officepickup.view", "marketing.interest.view"
        };
        var hrKeys = new[]
        {
            "rbac.department.manage", "rbac.unit.manage", "rbac.role.manage",
            "staff.view", "staff.create", "staff.kyc.review", "staff.kyc.approve", "staff.kyc.reject"
        };

        await EnsureRoleAsync(dbContext, "Super Admin", RoleType.SuperAdmin, allKeys, cancellationToken);
        await EnsureRoleAsync(dbContext, "Admin", RoleType.Admin, adminKeys, cancellationToken);
        await EnsureRoleAsync(dbContext, "HR", RoleType.HR, hrKeys, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureRoleAsync(
        IApplicationDbContext dbContext, string name, RoleType roleType, IEnumerable<string> permissionKeys, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Roles.AnyAsync(r => r.Name == name, cancellationToken);
        if (exists)
        {
            return;
        }

        dbContext.Roles.Add(Role.Create(name, roleType, permissionKeys));
    }
}

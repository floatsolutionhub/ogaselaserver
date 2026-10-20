using Ogasela.Application.Common.Interfaces;

namespace Ogasela.Application.Messaging;

/// <summary>
/// Content filtering for blocks: a viewer never sees ads from a seller they've blocked in feeds,
/// search or storefronts. Listings are keyed by SellerProfile id, blocks by User id - this bridges the two.
/// </summary>
public static class BlockedSellers
{
    /// <summary>SellerProfile ids of everyone the viewer has blocked (empty for anonymous viewers).</summary>
    public static IQueryable<Guid> ProfileIdsBlockedBy(this IApplicationDbContext dbContext, Guid? viewerId) =>
        from s in dbContext.SellerProfiles
        where dbContext.BlockedUsers.Any(b => b.BlockerId == viewerId && b.BlockedId == s.UserId)
        select s.Id;
}

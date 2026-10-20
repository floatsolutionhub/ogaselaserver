namespace Ogasela.Domain.Listings;

public enum ListingStatus
{
    Draft,
    Active,
    ExpiringSoon,
    Expired,
    Paused,
    Sold,

    /// <summary>Submitted for publishing and waiting for a moderator to approve it (goes Active) or reject it (back to Draft, with a ReviewNote). Not visible to buyers.</summary>
    PendingReview,

    /// <summary>
    /// Removed by its seller (from Draft, PendingReview or Expired). Kept, not erased, because
    /// chats, disputes, office pickups and payments still point at it - but nobody sees it, the
    /// seller included.
    /// </summary>
    Deleted
}

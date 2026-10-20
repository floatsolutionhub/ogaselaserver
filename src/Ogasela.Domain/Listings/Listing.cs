namespace Ogasela.Domain.Listings;

public class Listing
{
    /// <summary>
    /// Hard ceiling on MediaUrls - the most any PromotionPlan's PhotoLimit can be set to. A draft may
    /// hold up to this many; when it's submitted, <see cref="TrimMediaTo"/> keeps only what the
    /// chosen plan allows.
    /// </summary>
    public const int MaxPhotoCount = 30;

    private Listing()
    {
    }

    public Guid Id { get; private set; }

    public Guid SellerId { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>The plan this listing will publish (or last published) under. Not chosen yet on a fresh Draft.</summary>
    public Guid? PromotionPlanId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public decimal? Price { get; private set; }

    public ListingCondition Condition { get; private set; }

    public ListingStatus Status { get; private set; }

    /// <summary>Free-text place name (e.g. a city/area), used for text-based location filtering and display.</summary>
    public string? Location { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    /// <summary>
    /// Exposed as a settable List (rather than IReadOnlyList behind a private field) so EF
    /// Core's primitive-collection JSON mapping can materialize it directly on load.
    /// </summary>
    public List<string> MediaUrls { get; private set; } = new();

    /// <summary>
    /// A moderator's note to the seller: why the last review was rejected, or why the ad was taken
    /// down. Cleared when it's resubmitted, approved or reinstated.
    /// </summary>
    public string? ReviewNote { get; private set; }

    public DateTime? PublishedAt { get; private set; }

    public DateTime? ExpiresAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    /// <summary>Used items only: whether the seller has the original receipt of purchase. Null for new items
    /// or when the seller didn't say.</summary>
    public bool? HasOriginalReceipt { get; private set; }

    /// <summary>Optional links to the item on the seller's social accounts, shown on the ad.</summary>
    public string? TikTokUrl { get; private set; }

    public string? FacebookUrl { get; private set; }

    public string? InstagramUrl { get; private set; }

    /// <summary>Played inside the app on the ad screen.</summary>
    public string? YouTubeUrl { get; private set; }

    public const int SocialLinkMaxLength = 500;

    public static Listing CreateDraft(
        Guid id,
        Guid sellerId,
        Guid categoryId,
        Guid? promotionPlanId,
        string title,
        string description,
        decimal? price,
        ListingCondition condition,
        IEnumerable<string> mediaUrls,
        DateTime now)
    {
        var listing = new Listing
        {
            Id = id,
            SellerId = sellerId,
            CategoryId = categoryId,
            PromotionPlanId = promotionPlanId,
            Title = title,
            Description = description,
            Price = price,
            Condition = condition,
            Status = ListingStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };

        listing.MediaUrls.AddRange(mediaUrls);
        return listing;
    }

    /// <summary>
    /// Keeps the first <paramref name="limit"/> photos (the first is the cover) and drops the rest.
    /// Returns the dropped URLs, empty if nothing was over the limit.
    /// </summary>
    public IReadOnlyList<string> TrimMediaTo(int limit)
    {
        if (limit <= 0 || MediaUrls.Count <= limit)
        {
            return [];
        }

        var dropped = MediaUrls.Skip(limit).ToList();
        MediaUrls = MediaUrls.Take(limit).ToList();
        return dropped;
    }

    public void UpdateDetails(
        Guid categoryId,
        Guid? promotionPlanId,
        string title,
        string description,
        decimal? price,
        ListingCondition condition,
        IEnumerable<string> mediaUrls,
        DateTime now)
    {
        CategoryId = categoryId;
        PromotionPlanId = promotionPlanId;
        Title = title;
        Description = description;
        Price = price;
        Condition = condition;

        MediaUrls.Clear();
        MediaUrls.AddRange(mediaUrls);

        UpdatedAt = now;
    }

    /// <summary>Call after <see cref="CreateDraft"/>/<see cref="UpdateDetails"/>: the receipt flag only applies to used items.</summary>
    public void SetReceiptAndLinks(
        bool? hasOriginalReceipt, string? tikTokUrl, string? facebookUrl, string? instagramUrl, string? youTubeUrl, DateTime now)
    {
        HasOriginalReceipt = Condition == ListingCondition.Used ? hasOriginalReceipt : null;
        TikTokUrl = Clean(tikTokUrl);
        FacebookUrl = Clean(facebookUrl);
        InstagramUrl = Clean(instagramUrl);
        YouTubeUrl = Clean(youTubeUrl);
        UpdatedAt = now;

        static string? Clean(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
    }

    /// <summary>Records the plan picked at checkout; the listing stays a Draft until it's paid for.</summary>
    public void ChoosePlan(Guid promotionPlanId, DateTime now)
    {
        PromotionPlanId = promotionPlanId;
        UpdatedAt = now;
    }

    public void SetLocation(string? location, decimal? latitude, decimal? longitude, DateTime now)
    {
        Location = location;
        Latitude = latitude;
        Longitude = longitude;
        UpdatedAt = now;
    }

    public void Publish(DateTime publishedAt, DateTime expiresAt)
    {
        Status = ListingStatus.Active;
        PublishedAt = publishedAt;
        ExpiresAt = expiresAt;
        ReviewNote = null;
        UpdatedAt = publishedAt;
    }

    /// <summary>
    /// A paid-for (or free) submission - first time, or renewing an expired ad - now waits for a
    /// moderator. It starts a fresh plan period, so any old expiry date is dropped: the new one is
    /// set when it's approved.
    /// </summary>
    public void SubmitForReview(DateTime now)
    {
        Status = ListingStatus.PendingReview;
        ReviewNote = null;
        ExpiresAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// The ad has been live and its current plan period is still running (ExpiresAt is in the
    /// future): an edit of it, or a fix after such an edit was rejected. Not a new period.
    /// </summary>
    public bool HasRunningPlanPeriod(DateTime now) => PublishedAt is not null && ExpiresAt is { } expiresAt && expiresAt > now;

    /// <summary>
    /// Awaiting review as a new plan period (first time, or a renewal) rather than as an edit of an
    /// ad that has a period already: fresh submissions clear the expiry date, edits keep it.
    /// </summary>
    public bool IsFreshSubmission => ExpiresAt is null;

    /// <summary>
    /// A live ad was edited (or a rejected edit was fixed): it goes back to a moderator before
    /// buyers see the changes. Its plan period keeps running - the expiry date doesn't move.
    /// </summary>
    public void SubmitEditForReview(DateTime now)
    {
        Status = ListingStatus.PendingReview;
        ReviewNote = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Approved by a moderator. A fresh submission (no expiry yet) goes live and its plan period
    /// starts now. An approved edit goes back live on its existing period - or Expired if that
    /// ran out while it was being reviewed.
    /// </summary>
    public void ApproveReview(DateTime now, int planDurationDays)
    {
        if (ExpiresAt is not { } expiresAt)
        {
            Publish(now, now.AddDays(planDurationDays));
            return;
        }

        Status = expiresAt <= now ? ListingStatus.Expired : ListingStatus.Active;
        PublishedAt ??= now;
        ReviewNote = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// A paid plan change on an ad that has been live: the new plan takes effect immediately and
    /// its full duration starts counting now - the old expiry date no longer applies.
    /// </summary>
    public void ChangePlanNow(Guid promotionPlanId, int planDurationDays, DateTime now)
    {
        PromotionPlanId = promotionPlanId;
        ExpiresAt = now.AddDays(planDurationDays);
        UpdatedAt = now;
    }

    /// <summary>Statuses a seller may delete from: not live, not sold.</summary>
    public bool CanBeDeleted => Status is ListingStatus.Draft or ListingStatus.PendingReview or ListingStatus.Expired;

    public void Delete(DateTime now)
    {
        if (!CanBeDeleted)
        {
            throw new InvalidOperationException($"A {Status} listing can't be deleted.");
        }

        Status = ListingStatus.Deleted;
        UpdatedAt = now;
    }

    /// <summary>A moderator took a live ad off public view, with the reason shown to the seller.</summary>
    public void TakeDown(string reason, DateTime now)
    {
        Status = ListingStatus.Paused;
        ReviewNote = reason;
        UpdatedAt = now;
    }

    /// <summary>Back on public view after a takedown - or Expired if its time ran out meanwhile.</summary>
    public void Reinstate(DateTime now)
    {
        Status = ExpiresAt is { } expiresAt && expiresAt <= now ? ListingStatus.Expired : ListingStatus.Active;
        ReviewNote = null;
        UpdatedAt = now;
    }

    /// <summary>Moderator moves the ad to a more fitting category.</summary>
    public void ChangeCategory(Guid categoryId, DateTime now)
    {
        CategoryId = categoryId;
        UpdatedAt = now;
    }

    /// <summary>A moderator turned it down: back to Draft so the seller can fix it and submit again.</summary>
    public void RejectReview(string reason, DateTime now)
    {
        Status = ListingStatus.Draft;
        ReviewNote = reason;
        UpdatedAt = now;
    }

    public void Pause(DateTime now)
    {
        Status = ListingStatus.Paused;
        UpdatedAt = now;
    }

    public void MarkSold(DateTime now)
    {
        Status = ListingStatus.Sold;
        UpdatedAt = now;
    }

    public void MarkExpired(DateTime now)
    {
        Status = ListingStatus.Expired;
        UpdatedAt = now;
    }

    public void MarkExpiringSoon(DateTime now)
    {
        Status = ListingStatus.ExpiringSoon;
        UpdatedAt = now;
    }
}

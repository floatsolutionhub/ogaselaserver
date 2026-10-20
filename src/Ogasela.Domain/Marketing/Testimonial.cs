namespace Ogasela.Domain.Marketing;

/// <summary>
/// A quote from a real buyer or seller, shown on the ogasela.com homepage once published. Staff
/// enter it in the Control Portal and must confirm the person agreed to be quoted (ConsentConfirmedAt) -
/// invented reviews mislead buyers and can break consumer-protection rules.
/// </summary>
public class Testimonial
{
    public const int QuoteMaxLength = 400;
    public const int NameMaxLength = 80;
    public const int RoleMaxLength = 80;

    private Testimonial() { }

    public Guid Id { get; private set; }

    public string Quote { get; private set; } = string.Empty;

    /// <summary>As the person agreed to be named, e.g. "Chioma A.".</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>e.g. "Seller, Ikeja" or "Buyer, Wuse 2".</summary>
    public string Role { get; private set; } = string.Empty;

    /// <summary>1 to 5 stars.</summary>
    public int Rating { get; private set; }

    /// <summary>Only published testimonials appear on the website.</summary>
    public bool IsPublished { get; private set; }

    /// <summary>When a staff member confirmed the person agreed to be quoted.</summary>
    public DateTime ConsentConfirmedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? UpdatedAt { get; private set; }

    public static Testimonial Create(
        string quote, string name, string role, int rating, bool isPublished, Guid createdByUserId, DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            Quote = quote,
            Name = name,
            Role = role,
            Rating = rating,
            IsPublished = isPublished,
            ConsentConfirmedAt = now,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
        };

    public void Update(string quote, string name, string role, int rating, DateTime now)
    {
        Quote = quote;
        Name = name;
        Role = role;
        Rating = rating;
        UpdatedAt = now;
    }

    public void SetPublished(bool isPublished, DateTime now)
    {
        IsPublished = isPublished;
        UpdatedAt = now;
    }
}

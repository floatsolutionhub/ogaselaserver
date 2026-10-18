namespace Ogasela.Domain.Marketing;

/// <summary>Which ogasela.com form the visitor used.</summary>
public enum InterestKind
{
    /// <summary>"Join our testers" - wants pre-release builds to try out.</summary>
    Tester,

    /// <summary>"Join waiting list" - wants to hear when the app is in the stores.</summary>
    Waitlist
}

/// <summary>What the visitor plans to do on Ogasela.</summary>
public enum InterestIntent
{
    Buy,
    Sell,
    Both
}

/// <summary>The phone's operating system - which store to point a waiting-list signup at, or which build a tester needs.</summary>
public enum InterestDevicePlatform
{
    Android,
    IOS
}

/// <summary>How far staff have got with a signup in the Control Portal.</summary>
public enum InterestSignupStatus
{
    New,
    Contacted,
    Invited,
    Joined,
    Declined
}

/// <summary>
/// Someone who asked, from the marketing website, to test the app or to be told when it launches.
/// One row per (Kind, Email). Only kept on the visitor's consent (ConsentedAt), and deleted outright
/// when they ask - there's no soft delete, since keeping the contact details would defeat the request.
/// </summary>
public class InterestSignup
{
    private InterestSignup() { }

    public Guid Id { get; private set; }

    public InterestKind Kind { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Stored lower-case.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>Local 0XXXXXXXXXX format, same as User.Phone.</summary>
    public string? Phone { get; private set; }

    /// <summary>Nigerian state the visitor said they're in.</summary>
    public string? State { get; private set; }

    public InterestIntent? Intent { get; private set; }

    /// <summary>Asked on both forms. Null only for tester rows from before it was asked.</summary>
    public InterestDevicePlatform? DevicePlatform { get; private set; }

    public InterestSignupStatus Status { get; private set; }

    /// <summary>Staff-only notes, never shown to the visitor.</summary>
    public string? Notes { get; private set; }

    /// <summary>When the visitor ticked the consent box to be contacted about this.</summary>
    public DateTime ConsentedAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? UpdatedAt { get; private set; }

    public static InterestSignup Create(
        InterestKind kind,
        string name,
        string email,
        string? phone,
        string? state,
        InterestIntent? intent,
        InterestDevicePlatform? devicePlatform,
        DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = name,
            Email = email,
            Phone = phone,
            State = state,
            Intent = intent,
            DevicePlatform = devicePlatform,
            Status = InterestSignupStatus.New,
            ConsentedAt = now,
            CreatedAt = now,
        };

    public void UpdateStatus(InterestSignupStatus status, DateTime now)
    {
        Status = status;
        UpdatedAt = now;
    }

    public void UpdateNotes(string? notes, DateTime now)
    {
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        UpdatedAt = now;
    }
}

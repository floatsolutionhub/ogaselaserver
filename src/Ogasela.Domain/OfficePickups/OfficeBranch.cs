namespace Ogasela.Domain.OfficePickups;

/// <summary>
/// One of Ogasela's own offices where sellers drop items off and buyers collect them for an
/// office pickup. Managed from the Control Portal; only active branches are offered in the app,
/// nearest to the buyer first (by Latitude/Longitude).
/// </summary>
public class OfficeBranch
{
    private OfficeBranch()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Address { get; private set; } = string.Empty;

    public string State { get; private set; } = string.Empty;

    public string? City { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>Free text shown to buyers and sellers, e.g. "Mon-Sat, 9am-6pm".</summary>
    public string? OpeningHours { get; private set; }

    public decimal Latitude { get; private set; }

    public decimal Longitude { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public static OfficeBranch Create(
        string name, string address, string state, string? city, string? phone, string? openingHours,
        decimal latitude, decimal longitude, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = address,
        State = state,
        City = city,
        Phone = phone,
        OpeningHours = openingHours,
        Latitude = latitude,
        Longitude = longitude,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now
    };

    public void Update(
        string name, string address, string state, string? city, string? phone, string? openingHours,
        decimal latitude, decimal longitude, bool isActive, DateTime now)
    {
        Name = name;
        Address = address;
        State = state;
        City = city;
        Phone = phone;
        OpeningHours = openingHours;
        Latitude = latitude;
        Longitude = longitude;
        IsActive = isActive;
        UpdatedAt = now;
    }
}

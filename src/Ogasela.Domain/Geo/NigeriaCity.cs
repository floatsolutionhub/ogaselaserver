namespace Ogasela.Domain.Geo;

public class NigeriaCity
{
    private NigeriaCity()
    {
    }

    public Guid Id { get; private set; }

    public Guid StateId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Where the city is - used to find the city nearest a device. Null until staff set it.</summary>
    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public static NigeriaCity Create(Guid stateId, string name, decimal? latitude = null, decimal? longitude = null)
    {
        return new NigeriaCity
        {
            Id = Guid.NewGuid(),
            StateId = stateId,
            Name = name,
            Latitude = latitude,
            Longitude = longitude,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void Update(string name, decimal? latitude, decimal? longitude)
    {
        Name = name;
        SetCoordinates(latitude, longitude);
    }

    /// <summary>Both or neither - a city with only one coordinate can't be placed.</summary>
    public void SetCoordinates(decimal? latitude, decimal? longitude)
    {
        var complete = latitude is not null && longitude is not null;
        Latitude = complete ? latitude : null;
        Longitude = complete ? longitude : null;
    }
}

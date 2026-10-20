using Ogasela.Shared;

namespace Ogasela.Application.Geo;

public static class GeoErrors
{
    public static readonly Error StateNotFound = new(
        "Geo.StateNotFound", "The state could not be found.");

    public static readonly Error StateNameOrCodeTaken = new(
        "Geo.StateNameOrCodeTaken", "A state with this name or code already exists.");

    public static readonly Error CityNotFound = new(
        "Geo.CityNotFound", "The city could not be found.");

    public static readonly Error CityNameTaken = new(
        "Geo.CityNameTaken", "A city with this name already exists in this state.");

    public static readonly Error NoCityNearby = new(
        "Geo.NoCityNearby", "We couldn't find a city near you. Choose your state and city instead.");
}

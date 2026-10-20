using Microsoft.EntityFrameworkCore;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Geo;

namespace Ogasela.Infrastructure.Geo;

/// <summary>
/// Seeds the 36 Nigerian states + FCT, each with a handful of starter cities (with coordinates,
/// for "use my location"), so the Geo reference-data module isn't empty out of the box. Idempotent (upsert-by-Name for states,
/// upsert-by-Name-within-state for cities) and safe to run on every startup, same as RbacSeeder.
/// </summary>
public static class NigeriaGeoSeeder
{
    private static readonly (string Name, string Code, (string Name, decimal Latitude, decimal Longitude)[] Cities)[] States =
    [
        ("Abia", "AB", [("Umuahia", 5.5250m, 7.4922m), ("Aba", 5.1066m, 7.3667m), ("Ohafia", 5.6150m, 7.8340m), ("Arochukwu", 5.3890m, 7.9100m)]),
        ("Adamawa", "AD", [("Yola", 9.2035m, 12.4954m), ("Mubi", 10.2676m, 13.2644m), ("Numan", 9.4669m, 12.0328m), ("Jimeta", 9.2799m, 12.4581m)]),
        ("Akwa Ibom", "AK", [("Uyo", 5.0377m, 7.9128m), ("Eket", 4.6423m, 7.9244m), ("Ikot Ekpene", 5.1794m, 7.7147m), ("Oron", 4.8270m, 8.2340m)]),
        ("Anambra", "AN", [("Awka", 6.2104m, 7.0741m), ("Onitsha", 6.1498m, 6.7857m), ("Nnewi", 6.0177m, 6.9177m), ("Ekwulobia", 6.0230m, 7.0830m)]),
        ("Bauchi", "BA", [("Bauchi", 10.3103m, 9.8439m), ("Azare", 11.6765m, 10.1948m), ("Misau", 11.3136m, 10.4666m), ("Jama'are", 11.6700m, 9.9300m)]),
        ("Bayelsa", "BY", [("Yenagoa", 4.9267m, 6.2676m), ("Brass", 4.3150m, 6.2410m), ("Ogbia", 4.6880m, 6.3100m), ("Sagbama", 5.1600m, 6.2000m)]),
        ("Benue", "BE", [("Makurdi", 7.7322m, 8.5391m), ("Gboko", 7.3240m, 9.0030m), ("Otukpo", 7.1904m, 8.1300m), ("Katsina-Ala", 7.1667m, 9.2833m)]),
        ("Borno", "BO", [("Maiduguri", 11.8311m, 13.1510m), ("Bama", 11.5221m, 13.6856m), ("Dikwa", 12.0360m, 13.9180m), ("Gwoza", 11.0830m, 13.6950m)]),
        ("Cross River", "CR", [("Calabar", 4.9757m, 8.3417m), ("Ikom", 5.9670m, 8.7100m), ("Ogoja", 6.6550m, 8.7990m), ("Obudu", 6.6667m, 9.1667m)]),
        ("Delta", "DE", [("Asaba", 6.1980m, 6.7310m), ("Warri", 5.5167m, 5.7500m), ("Sapele", 5.8941m, 5.6767m), ("Ughelli", 5.4899m, 5.9918m)]),
        ("Ebonyi", "EB", [("Abakaliki", 6.3249m, 8.1137m), ("Afikpo", 5.8925m, 7.9354m), ("Onueke", 6.1500m, 8.0300m), ("Ezza", 6.2200m, 8.0800m)]),
        ("Edo", "ED", [("Benin City", 6.3350m, 5.6037m), ("Auchi", 7.0676m, 6.2636m), ("Ekpoma", 6.7430m, 6.1390m), ("Uromi", 6.7000m, 6.3333m)]),
        ("Ekiti", "EK", [("Ado-Ekiti", 7.6211m, 5.2214m), ("Ikere-Ekiti", 7.4980m, 5.2320m), ("Ijero-Ekiti", 7.8130m, 5.0710m), ("Oye", 7.7950m, 5.3280m)]),
        ("Enugu", "EN", [("Enugu", 6.4584m, 7.5464m), ("Nsukka", 6.8567m, 7.3958m), ("Awgu", 6.0750m, 7.4780m), ("Agbani", 6.3070m, 7.5520m)]),
        ("FCT", "FC", [("Abuja", 9.0579m, 7.4951m), ("Gwagwalada", 8.9420m, 7.0830m), ("Kuje", 8.8790m, 7.2270m), ("Bwari", 9.2830m, 7.3800m)]),
        ("Gombe", "GO", [("Gombe", 10.2897m, 11.1673m), ("Kumo", 10.0450m, 11.2130m), ("Billiri", 9.8650m, 11.2250m), ("Dukku", 10.8230m, 10.7720m)]),
        ("Imo", "IM", [("Owerri", 5.4836m, 7.0333m), ("Orlu", 5.7957m, 7.0351m), ("Okigwe", 5.8293m, 7.3507m), ("Mbaise", 5.4500m, 7.2500m)]),
        ("Jigawa", "JI", [("Dutse", 11.7562m, 9.3389m), ("Hadejia", 12.4498m, 10.0444m), ("Gumel", 12.6270m, 9.3880m), ("Birnin Kudu", 11.4510m, 9.4830m)]),
        ("Kaduna", "KD", [("Kaduna", 10.5222m, 7.4383m), ("Zaria", 11.0855m, 7.7199m), ("Kafanchan", 9.5833m, 8.3000m), ("Kagoro", 9.6000m, 8.3900m)]),
        ("Kano", "KN", [("Kano", 12.0022m, 8.5920m), ("Wudil", 11.8090m, 8.8380m), ("Gwarzo", 11.9160m, 7.9330m), ("Rano", 11.5560m, 8.5830m)]),
        ("Katsina", "KT", [("Katsina", 12.9908m, 7.6018m), ("Funtua", 11.5233m, 7.3081m), ("Daura", 13.0360m, 8.3180m), ("Malumfashi", 11.7900m, 7.6200m)]),
        ("Kebbi", "KE", [("Birnin Kebbi", 12.4539m, 4.1975m), ("Argungu", 12.7448m, 4.5251m), ("Yauri", 10.8300m, 4.7400m), ("Zuru", 11.4350m, 5.2300m)]),
        ("Kogi", "KO", [("Lokoja", 7.8023m, 6.7333m), ("Okene", 7.5500m, 6.2350m), ("Idah", 7.1100m, 6.7300m), ("Kabba", 7.8300m, 6.0700m)]),
        ("Kwara", "KW", [("Ilorin", 8.4966m, 4.5421m), ("Offa", 8.1491m, 4.7207m), ("Omu-Aran", 8.1390m, 5.1020m), ("Jebba", 9.1330m, 4.8240m)]),
        // Lagos is the biggest market, so it gets its local government areas - with only a handful of
        // cities, "use my location" in e.g. Alimosho would land on Ota across the Ogun border.
        ("Lagos", "LA", [("Ikeja", 6.6018m, 3.3515m), ("Lagos Island", 6.4549m, 3.3940m), ("Lekki", 6.4698m, 3.5852m), ("Badagry", 6.4316m, 2.8876m), ("Agege", 6.6180m, 3.3209m), ("Ajeromi-Ifelodun", 6.4550m, 3.3340m), ("Alimosho", 6.5960m, 3.2560m), ("Amuwo-Odofin", 6.4600m, 3.2830m), ("Apapa", 6.4489m, 3.3590m), ("Epe", 6.5841m, 3.9834m), ("Eti-Osa", 6.4550m, 3.5200m), ("Ibeju-Lekki", 6.4600m, 3.8500m), ("Ifako-Ijaiye", 6.6400m, 3.3200m), ("Ikorodu", 6.6194m, 3.5105m), ("Kosofe", 6.5800m, 3.3900m), ("Lagos Mainland", 6.4950m, 3.3800m), ("Mushin", 6.5273m, 3.3414m), ("Ojo", 6.4667m, 3.1833m), ("Oshodi-Isolo", 6.5450m, 3.3300m), ("Shomolu", 6.5392m, 3.3842m), ("Surulere", 6.5000m, 3.3500m)]),
        ("Nasarawa", "NA", [("Lafia", 8.4939m, 8.5153m), ("Keffi", 8.8486m, 7.8736m), ("Akwanga", 8.9100m, 8.3900m), ("Nasarawa", 8.5400m, 7.7100m)]),
        ("Niger", "NI", [("Minna", 9.6139m, 6.5569m), ("Bida", 9.0804m, 6.0100m), ("Kontagora", 10.4000m, 5.4700m), ("Suleja", 9.1806m, 7.1794m)]),
        ("Ogun", "OG", [("Abeokuta", 7.1475m, 3.3619m), ("Sagamu", 6.8322m, 3.6319m), ("Ijebu-Ode", 6.8194m, 3.9173m), ("Ota", 6.6804m, 3.2356m)]),
        ("Ondo", "ON", [("Akure", 7.2571m, 5.2058m), ("Ondo City", 7.0932m, 4.8353m), ("Owo", 7.1962m, 5.5868m), ("Ikare", 7.5304m, 5.7600m)]),
        ("Osun", "OS", [("Osogbo", 7.7827m, 4.5418m), ("Ile-Ife", 7.4824m, 4.5603m), ("Ilesa", 7.6280m, 4.7416m), ("Ede", 7.7390m, 4.4360m)]),
        ("Oyo", "OY", [("Ibadan", 7.3775m, 3.9470m), ("Ogbomoso", 8.1335m, 4.2407m), ("Oyo Town", 7.8526m, 3.9312m), ("Iseyin", 7.9700m, 3.5900m)]),
        ("Plateau", "PL", [("Jos", 9.8965m, 8.8583m), ("Bukuru", 9.7900m, 8.8600m), ("Pankshin", 9.3260m, 9.4380m), ("Shendam", 8.8830m, 9.5330m)]),
        ("Rivers", "RI", [("Port Harcourt", 4.8156m, 7.0498m), ("Bonny", 4.4500m, 7.1667m), ("Okrika", 4.7400m, 7.0830m), ("Ahoada", 5.0700m, 6.6500m)]),
        ("Sokoto", "SO", [("Sokoto", 13.0059m, 5.2476m), ("Wurno", 13.2900m, 5.4300m), ("Tambuwal", 12.4060m, 4.6460m), ("Gwadabawa", 13.3580m, 5.2370m)]),
        ("Taraba", "TA", [("Jalingo", 8.8937m, 11.3596m), ("Wukari", 7.8710m, 9.7780m), ("Bali", 7.8550m, 10.9680m), ("Gembu", 6.7050m, 11.2600m)]),
        ("Yobe", "YO", [("Damaturu", 11.7470m, 11.9608m), ("Potiskum", 11.7091m, 11.0694m), ("Gashua", 12.8710m, 11.0460m), ("Nguru", 12.8790m, 10.4520m)]),
        ("Zamfara", "ZA", [("Gusau", 12.1628m, 6.6614m), ("Kaura Namoda", 12.5930m, 6.5870m), ("Talata Mafara", 12.5670m, 6.0670m), ("Anka", 12.1100m, 5.9300m)])
    ];

    public static async Task SeedAsync(IApplicationDbContext dbContext, CancellationToken cancellationToken)
    {
        var existingStateNames = await dbContext.NigeriaStates.Select(s => s.Name).ToListAsync(cancellationToken);

        foreach (var (name, code, _) in States)
        {
            if (!existingStateNames.Contains(name))
            {
                dbContext.NigeriaStates.Add(NigeriaState.Create(name, code));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var stateIdsByName = await dbContext.NigeriaStates.ToDictionaryAsync(s => s.Name, s => s.Id, cancellationToken);

        foreach (var (name, _, cities) in States)
        {
            var stateId = stateIdsByName[name];
            var existingCities = await dbContext.NigeriaCities
                .Where(c => c.StateId == stateId)
                .ToListAsync(cancellationToken);

            foreach (var (cityName, latitude, longitude) in cities)
            {
                var existing = existingCities.FirstOrDefault(c => c.Name == cityName);
                if (existing is null)
                {
                    dbContext.NigeriaCities.Add(NigeriaCity.Create(stateId, cityName, latitude, longitude));
                }
                else if (existing.Latitude is null || existing.Longitude is null)
                {
                    // Backfills cities seeded before they had coordinates; never overwrites staff edits.
                    existing.SetCoordinates(latitude, longitude);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Ogasela.Application.Search;
using Ogasela.Domain.Listings;
using Ogasela.Domain.Promotions;
using Ogasela.Infrastructure.Persistence;

namespace Ogasela.Infrastructure.Search;

/// <summary>
/// Implements <see cref="IListingSearchRepository"/> with a single hand-written SQL query
/// against the tsvector/pg_trgm infrastructure set up in the AddListingSearch migration. This
/// intentionally bypasses LINQ - the ranking formula and optional Haversine distance calculation
/// don't translate cleanly, and a single query with null-coalescing filters is both simpler and
/// faster than composing/branching an IQueryable for every optional filter combination.
/// </summary>
public sealed class PostgresListingSearchRepository : IListingSearchRepository
{
    /// <summary>
    /// Ranking formula: Score = (0.60 * TextRank) + (0.25 * NormalizedBoostWeight) + (0.15 * RecencyFactor).
    ///   - TextRank: Postgres's ts_rank() against the maintained search_vector (0 when no keyword given).
    ///   - NormalizedBoostWeight: PromotionPlan.BoostWeight / 3 (3 is Premium's weight - the seeded max), so a
    ///     higher-tier plan always outranks a lower one at equal text relevance, without dominating a genuinely
    ///     better text match.
    ///   - RecencyFactor: 1 / (1 + days since publish), so newer listings rank slightly higher but relevance
    ///     and plan tier still dominate for anything more than a few days old.
    /// To retune, change the three literal weights below (they're meant to sum to 1.0) - the query structure
    /// itself never needs to change.
    /// </summary>
    private const string SearchSql = """
        WITH matches AS (
            SELECT
                l."Id",
                l."SellerId",
                l."CategoryId",
                l."Title",
                l."Description",
                l."MediaUrls"[1] AS "ThumbnailUrl",
                l."Price",
                l."Condition",
                l."Location",
                l."Latitude",
                l."Longitude",
                l."PublishedAt",
                l."CreatedAt",
                pp."Name" AS "PlanName",
                pp."BoostWeight",
                GREATEST(
                    COALESCE(ts_rank(l."SearchVector", websearch_to_tsquery('english', @keyword)), 0),
                    CASE WHEN @prefixQuery IS NULL THEN 0
                         ELSE COALESCE(ts_rank(l."SearchVector", to_tsquery('english', @prefixQuery)), 0) END
                )::numeric AS "TextRank",
                CASE
                    WHEN @latitude IS NOT NULL AND @longitude IS NOT NULL
                         AND l."Latitude" IS NOT NULL AND l."Longitude" IS NOT NULL
                    THEN 6371 * acos(LEAST(1.0, GREATEST(-1.0,
                            cos(radians(@latitude)) * cos(radians(l."Latitude")) * cos(radians(l."Longitude") - radians(@longitude))
                            + sin(radians(@latitude)) * sin(radians(l."Latitude"))
                         )))
                    ELSE NULL
                END AS "DistanceKm",
                COUNT(*) OVER() AS "TotalCount"
            FROM "Listings" l
            JOIN "PromotionPlans" pp ON pp."Id" = l."PromotionPlanId"
            JOIN "SellerProfiles" sp ON sp."Id" = l."SellerId"
            WHERE l."Status" = 'Active'
                AND (@keyword IS NULL
                     OR l."SearchVector" @@ websearch_to_tsquery('english', @keyword)
                     OR (@prefixQuery IS NOT NULL AND l."SearchVector" @@ to_tsquery('english', @prefixQuery))
                     OR similarity(l."Title", @keyword) > 0.2)
                AND (@categoryId IS NULL
                     OR l."CategoryId" = @categoryId
                     OR l."CategoryId" IN (SELECT c."Id" FROM "Categories" c WHERE c."ParentCategoryId" = @categoryId))
                AND (@minPrice IS NULL OR l."Price" >= @minPrice)
                AND (@maxPrice IS NULL OR l."Price" <= @maxPrice)
                AND (@condition IS NULL OR l."Condition" = @condition)
                AND (@verifiedOnly = FALSE OR sp."VerificationStatus" = 'Verified')
                AND (@location IS NULL OR l."Location" ILIKE '%' || @location || '%')
                AND (@viewerId IS NULL OR NOT EXISTS (
                    SELECT 1 FROM "BlockedUsers" b WHERE b."BlockerId" = @viewerId AND b."BlockedId" = sp."UserId"))
                AND (
                    @radiusKm IS NULL OR @latitude IS NULL OR @longitude IS NULL
                    OR l."Latitude" IS NULL OR l."Longitude" IS NULL
                    OR 6371 * acos(LEAST(1.0, GREATEST(-1.0,
                            cos(radians(@latitude)) * cos(radians(l."Latitude")) * cos(radians(l."Longitude") - radians(@longitude))
                            + sin(radians(@latitude)) * sin(radians(l."Latitude"))
                       ))) <= @radiusKm
                )
        )
        SELECT *,
            (0.60 * "TextRank")
            + (0.25 * (LEAST("BoostWeight", 3)::numeric / 3.0))
            + (0.15 * (1.0 / (1.0 + (EXTRACT(EPOCH FROM (now() - COALESCE("PublishedAt", "CreatedAt"))) / 86400.0))))
            AS "Score"
        FROM matches
        ORDER BY {0}
        LIMIT @pageSize OFFSET @offset;
        """;

    private readonly OgaselaDbContext _dbContext;

    public PostgresListingSearchRepository(OgaselaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SearchResultPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;

        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = string.Format(SearchSql, BuildOrderByClause(query.SortBy));

            AddParameter(command, "keyword", NpgsqlDbType.Text, query.Keyword);
            AddParameter(command, "prefixQuery", NpgsqlDbType.Text, BuildPrefixQuery(query.Keyword));
            AddParameter(command, "categoryId", NpgsqlDbType.Uuid, query.CategoryId);
            AddParameter(command, "minPrice", NpgsqlDbType.Numeric, query.MinPrice);
            AddParameter(command, "maxPrice", NpgsqlDbType.Numeric, query.MaxPrice);
            AddParameter(command, "condition", NpgsqlDbType.Text, query.Condition?.ToString());
            AddParameter(command, "verifiedOnly", NpgsqlDbType.Boolean, query.VerifiedSellerOnly);
            AddParameter(command, "location", NpgsqlDbType.Text, query.Location);
            AddParameter(command, "viewerId", NpgsqlDbType.Uuid, query.ViewerUserId);
            AddParameter(command, "latitude", NpgsqlDbType.Numeric, query.Latitude);
            AddParameter(command, "longitude", NpgsqlDbType.Numeric, query.Longitude);
            AddParameter(command, "radiusKm", NpgsqlDbType.Numeric, query.RadiusKm);
            AddParameter(command, "pageSize", NpgsqlDbType.Integer, query.PageSize);
            AddParameter(command, "offset", NpgsqlDbType.Integer, (query.Page - 1) * query.PageSize);

            var items = new List<SearchResultItem>();
            var totalCount = 0;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                totalCount = (int)reader.GetInt64(reader.GetOrdinal("TotalCount"));

                items.Add(new SearchResultItem(
                    reader.GetGuid(reader.GetOrdinal("Id")),
                    reader.GetGuid(reader.GetOrdinal("SellerId")),
                    reader.GetGuid(reader.GetOrdinal("CategoryId")),
                    reader.GetString(reader.GetOrdinal("Title")),
                    reader.GetString(reader.GetOrdinal("Description")),
                    GetNullableString(reader, "ThumbnailUrl"),
                    GetNullable<decimal>(reader, "Price"),
                    Enum.Parse<ListingCondition>(reader.GetString(reader.GetOrdinal("Condition"))),
                    GetNullableString(reader, "Location"),
                    GetNullable<decimal>(reader, "Latitude"),
                    GetNullable<decimal>(reader, "Longitude"),
                    GetNullable<DateTime>(reader, "PublishedAt"),
                    reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                    reader.GetString(reader.GetOrdinal("PlanName")),
                    GetNullable<double>(reader, "DistanceKm"),
                    reader.GetDecimal(reader.GetOrdinal("Score"))));
            }

            return new SearchResultPage(items, query.Page, query.PageSize, totalCount);
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string BuildOrderByClause(SearchSortOption sortBy) => sortBy switch
    {
        // "Id" is appended to every branch as a final tie-breaker so pagination is stable even
        // when many rows share the same score/price/date/distance.
        SearchSortOption.Newest => "\"PublishedAt\" DESC NULLS LAST, \"CreatedAt\" DESC, \"Id\"",
        SearchSortOption.PriceAsc => "\"Price\" ASC NULLS LAST, \"Id\"",
        SearchSortOption.PriceDesc => "\"Price\" DESC NULLS LAST, \"Id\"",
        SearchSortOption.Distance => "\"DistanceKm\" ASC NULLS LAST, \"Id\"",
        _ => "\"Score\" DESC, \"Id\""
    };

    /// <summary>
    /// Search-as-you-type support: "sam t" becomes "sam:* &amp; t:*", so a partially typed word
    /// still matches ("sam" finds "Samsung", "lapt" finds "laptop"). websearch_to_tsquery alone
    /// only matches whole words. Each token is reduced to letters/digits first, so nothing typed
    /// can inject tsquery operators; null when no token of 2+ characters is left.
    /// </summary>
    private static string? BuildPrefixQuery(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return null;
        }

        var tokens = Regex.Split(keyword.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            // A lone letter as a prefix ("c:*") matches nearly everything, so it's ignored here;
            // whole-word matching via websearch_to_tsquery still sees it.
            .Where(token => token.Length >= 2)
            .Take(8)
            .Select(token => $"{token}:*")
            .ToList();

        return tokens.Count == 0 ? null : string.Join(" & ", tokens);
    }

    private static void AddParameter(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        var parameter = new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value };
        command.Parameters.Add(parameter);
    }

    private static T? GetNullable<T>(NpgsqlDataReader reader, string column) where T : struct
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    private static string? GetNullableString(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}

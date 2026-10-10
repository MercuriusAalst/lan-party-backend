using System.Data.Common;
using System.Text.RegularExpressions;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Discovery;
using Mercurius.Modules.Discovery.Contracts;
using Mercurius.Modules.Discovery.Domain;
using Mercurius.Modules.Discovery.Infrastructure;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Shared.Search;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Mercurius.Modules.Discovery.Tests;

public class DiscoverySearchOptimizationTests
{
    [Theory]
    [InlineData("abc", "abd")]
    [InlineData("", null)]
    [InlineData("abc\uD7FF", null)]
    [InlineData("abc\uFFFF", null)]
    public void GetPrefixUpperBound_HandlesSafeAndUnincrementablePrefixes(string prefix, string? expected)
    {
        Assert.Equal(expected, SearchRequest.GetPrefixUpperBound(prefix));
    }

    [Fact]
    public void GetPrefixUpperBound_ReturnsNullForLoneSurrogateLastChar()
    {
        // Lone surrogates cannot round-trip through xUnit theory data (UTF-8), so build them in-process.
        Assert.Null(SearchRequest.GetPrefixUpperBound("abc" + '\uD83D'));
        Assert.Null(SearchRequest.GetPrefixUpperBound("abc" + '\uDE00'));
    }

    [Fact]
    public void SearchDocumentModel_UsesTheSingleActiveOrderedBtree()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql("Host=localhost;Database=translation-only")
            .Options;
        using var dbContext = new MercuriusDBContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(SearchDocument))!;
        var normalizedText = entity.FindProperty(nameof(SearchDocument.NormalizedText))!;
        var activeTextIndex = entity.GetIndexes().Single(index => index.GetDatabaseName() == "IX_search_documents_active_text");

        Assert.Equal("C", normalizedText.GetCollation());
        Assert.Equal("is_deleted = false", activeTextIndex.GetFilter());
        Assert.DoesNotContain("entity_type", activeTextIndex.GetFilter(), StringComparison.Ordinal);
        Assert.Single(entity.GetIndexes(), index => index.GetFilter() == "is_deleted = false");
        Assert.DoesNotContain("active_exact_order", entity.GetIndexes().Select(index => index.GetDatabaseName()));
    }

    [Fact]
    public async Task SearchAsync_UsesScalarPrefixBoundsAndTupleCursorUnderGenericPlans()
    {
        await using var database = PostgresTestDatabase.Create();
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            MaxAutoPrepare = 10,
            AutoPrepareMinUsages = 1
        }.ConnectionString;
        var sqlCapture = new SearchSqlCaptureInterceptor();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(sqlCapture)
            .Options;

        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();
        dbContext.Set<SearchDocument>().AddRange(
            Document(SearchDocumentTypes.User, "tea", "tea"),
            Document(SearchDocumentTypes.User, "tea fig", "tea fig"),
            Document(SearchDocumentTypes.Team, "tea zed", "tea zed"),
            Document(SearchDocumentTypes.User, "outside range", "teb"),
            Document(SearchDocumentTypes.User, "short label", "te"),
            Document(SearchDocumentTypes.Sponsor, "hidden sponsor", "tea sponsor", typeOrder: 0),
            Document(SearchDocumentTypes.User, "private user", "tea private", isDeleted: true),
            Document(SearchDocumentTypes.User, "contains only", "from tea"));
        await dbContext.SaveChangesAsync();
        sqlCapture.Commands.Clear();

        var search = new DiscoveryModuleFacade(
            new DiscoveryDbContextAdapter<MercuriusDBContext>(dbContext),
            null!);
        var exact = await search.SearchAsync(new DiscoverySearchRequest("tea", null, 1));
        var firstPrefix = await search.SearchAsync(new DiscoverySearchRequest("tea", exact.NextCursor, 1));
        var baselineDeepPage = await search.SearchAsync(new DiscoverySearchRequest("tea", firstPrefix.NextCursor, 1));
        Assert.Equal("tea", Assert.Single(exact.Results).DisplayLabel);
        Assert.Equal("tea fig", Assert.Single(firstPrefix.Results).DisplayLabel);
        Assert.Equal("tea zed", Assert.Single(baselineDeepPage.Results).DisplayLabel);
        var contains = await search.SearchAsync(new DiscoverySearchRequest("tea", baselineDeepPage.NextCursor, 1));
        Assert.Equal("contains only", Assert.Single(contains.Results).DisplayLabel);
        Assert.False(contains.HasMore);

        await dbContext.Database.OpenConnectionAsync();
        await dbContext.Database.ExecuteSqlRawAsync("SET plan_cache_mode = force_generic_plan;");
        for (var i = 0; i < 3; i++)
        {
            var genericPage = await search.SearchAsync(new DiscoverySearchRequest("tea", firstPrefix.NextCursor, 1));
            Assert.Equal("tea zed", Assert.Single(genericPage.Results).DisplayLabel);
        }

        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(generic_plans), 0)
            FROM pg_prepared_statements
            WHERE statement LIKE '%normalized_text%' AND statement LIKE '%LIKE%'
            """;
        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) > 0, "The query did not execute through a forced generic prepared plan.");

        var deepPrefixSql = sqlCapture.Commands.Last(commandText =>
            commandText.Contains("LIKE", StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains(" > ", StringComparison.Ordinal));
        Assert.Matches(new Regex("normalized_text\\\"?\\s*>=\\s*[$@]"), deepPrefixSql);
        Assert.Matches(new Regex("normalized_text\\\"?\\s*<\\s*[$@]"), deepPrefixSql);
        Assert.Contains("LIKE", deepPrefixSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE", deepPrefixSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entity_id", deepPrefixSql, StringComparison.Ordinal);
        Assert.DoesNotContain("CASE", deepPrefixSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CAST", deepPrefixSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"type_order\"::", deepPrefixSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'tea'", deepPrefixSql, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(
            deepPrefixSql,
            "(?:ROW\\s*)?\\(\\s*(?:\\w+\\.)?\"?normalized_text\"?\\s*,\\s*(?:\\w+\\.)?\"?type_order\"?\\s*,\\s*(?:\\w+\\.)?\"?entity_id\"?\\s*\\)\\s*>\\s*(?:ROW\\s*)?\\("));

        sqlCapture.Commands.Clear();
        dbContext.Set<SearchDocument>().Add(Document(SearchDocumentTypes.User, "emoji prefix", "tea😀 tail"));
        await dbContext.SaveChangesAsync();
        var unicodePrefix = await search.SearchAsync(new DiscoverySearchRequest("tea😀", null, 10));
        Assert.Equal("emoji prefix", Assert.Single(unicodePrefix.Results).DisplayLabel);
        var unicodePrefixSql = sqlCapture.Commands.First(commandText =>
            commandText.Contains("LIKE", StringComparison.OrdinalIgnoreCase));
        Assert.Matches(new Regex("normalized_text\\\"?\\s*>=\\s*[$@]"), unicodePrefixSql);
        Assert.DoesNotMatch(new Regex("normalized_text\\\"?\\s*<\\s*[$@]"), unicodePrefixSql);
    }

    [Fact]
    public async Task SearchAsync_OrdersNonAsciiLabelsByCBytes()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();
        dbContext.Set<SearchDocument>().AddRange(
            Document(SearchDocumentTypes.Team, "team émile", "team émile"),
            Document(SearchDocumentTypes.Team, "team zed", "team zed"));
        await dbContext.SaveChangesAsync();

        var search = new DiscoveryModuleFacade(
            new DiscoveryDbContextAdapter<MercuriusDBContext>(dbContext),
            null!);
        var results = await search.SearchAsync(new DiscoverySearchRequest("team", null, 10));

        Assert.Equal(["team zed", "team émile"], results.Results.Select(result => result.DisplayLabel));
    }

    [Fact]
    public async Task SearchAsync_PagesEveryMatchExactlyOnceAcrossRankBoundariesAndOrderTies()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();

        dbContext.Set<SearchDocument>().AddRange(
            // Exact pass: four rows share normalized_text "tea", so type_order then entity_id decide order.
            Document(SearchDocumentTypes.User, "u-exact", "tea", entityId: "00000000-0000-0000-0000-000000000101"),
            Document(SearchDocumentTypes.Team, "t-exact-a", "tea", entityId: "00000000-0000-0000-0000-000000000102"),
            Document(SearchDocumentTypes.Team, "t-exact-b", "tea", entityId: "00000000-0000-0000-0000-000000000103"),
            Document(SearchDocumentTypes.Tournament, "n-exact", "tea", entityId: "00000000-0000-0000-0000-000000000104"),
            // Starts-with pass.
            Document(SearchDocumentTypes.User, "p1", "tea1", entityId: "00000000-0000-0000-0000-000000000201"),
            Document(SearchDocumentTypes.Team, "p2", "tea2", entityId: "00000000-0000-0000-0000-000000000202"),
            Document(SearchDocumentTypes.Tournament, "p3", "tea3", entityId: "00000000-0000-0000-0000-000000000203"),
            Document(SearchDocumentTypes.User, "p4", "tea4", entityId: "00000000-0000-0000-0000-000000000204"),
            Document(SearchDocumentTypes.User, "p5", "tea5", entityId: "00000000-0000-0000-0000-000000000205"),
            // Contains pass, with another normalized_text tie.
            Document(SearchDocumentTypes.User, "c-green-user", "green tea", entityId: "00000000-0000-0000-0000-000000000301"),
            Document(SearchDocumentTypes.Team, "c-green-team", "green tea", entityId: "00000000-0000-0000-0000-000000000302"),
            Document(SearchDocumentTypes.User, "c-iced", "iced tea", entityId: "00000000-0000-0000-0000-000000000303"),
            Document(SearchDocumentTypes.User, "c-mint", "mint tea", entityId: "00000000-0000-0000-0000-000000000304"),
            Document(SearchDocumentTypes.User, "c-oolong", "oolong tea", entityId: "00000000-0000-0000-0000-000000000305"),
            Document(SearchDocumentTypes.User, "c-rooibos", "rooibos tea", entityId: "00000000-0000-0000-0000-000000000306"));
        await dbContext.SaveChangesAsync();

        var search = new DiscoveryModuleFacade(
            new DiscoveryDbContextAdapter<MercuriusDBContext>(dbContext),
            null!);

        string[] expected =
        [
            "u-exact", "t-exact-a", "t-exact-b", "n-exact",
            "p1", "p2", "p3", "p4", "p5",
            "c-green-user", "c-green-team", "c-iced", "c-mint", "c-oolong", "c-rooibos"
        ];

        var singlePage = await search.SearchAsync(new DiscoverySearchRequest("tea", null, expected.Length));
        Assert.False(singlePage.HasMore);
        Assert.Equal(expected, singlePage.Results.Select(result => result.DisplayLabel));

        var pagedLabels = new List<string>();
        string? cursor = null;
        bool hasMore;
        do
        {
            var page = await search.SearchAsync(new DiscoverySearchRequest("tea", cursor, 2));
            Assert.InRange(page.Results.Count, 1, 2);
            pagedLabels.AddRange(page.Results.Select(result => result.DisplayLabel));
            cursor = page.NextCursor;
            hasMore = page.HasMore;
        }
        while (hasMore);

        Assert.Equal(expected, pagedLabels);
        Assert.Equal(expected.Length, pagedLabels.Distinct().Count());
    }

    [Fact]
    public async Task SearchAsync_PagesEligibleDocumentsWhoseStoredTypeOrderIsOutsideTheCanonicalRange()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using var dbContext = new MercuriusDBContext(options);
        await dbContext.Database.MigrateAsync();

        // Eligibility is decided by entity type, so a stored type_order outside the canonical 0..2
        // range must still page; the smallint column also allows both short boundaries.
        dbContext.Set<SearchDocument>().AddRange(
            Document(SearchDocumentTypes.User, "q-lowest", "tea", typeOrder: short.MinValue, entityId: "00000000-0000-0000-0000-000000000401"),
            Document(SearchDocumentTypes.User, "q-zero", "tea", typeOrder: 0, entityId: "00000000-0000-0000-0000-000000000402"),
            Document(SearchDocumentTypes.Team, "q-three", "tea", typeOrder: 3, entityId: "00000000-0000-0000-0000-000000000403"),
            Document(SearchDocumentTypes.Tournament, "q-highest", "tea", typeOrder: short.MaxValue, entityId: "00000000-0000-0000-0000-000000000404"),
            Document(SearchDocumentTypes.User, "q-later", "tea later", entityId: "00000000-0000-0000-0000-000000000405"));
        await dbContext.SaveChangesAsync();

        var search = new DiscoveryModuleFacade(
            new DiscoveryDbContextAdapter<MercuriusDBContext>(dbContext),
            null!);

        string[] expected = ["q-lowest", "q-zero", "q-three", "q-highest", "q-later"];

        var pagedLabels = new List<string>();
        string? cursor = null;
        var hasMore = true;
        for (var page = 0; hasMore && page <= expected.Length; page++)
        {
            var result = await search.SearchAsync(new DiscoverySearchRequest("tea", cursor, 1));
            pagedLabels.AddRange(result.Results.Select(item => item.DisplayLabel));
            cursor = result.NextCursor;
            hasMore = result.HasMore;
        }

        Assert.False(hasMore);
        Assert.Equal(expected, pagedLabels);

        // A tampered cursor whose type order cannot come from the smallint column is still rejected.
        var outOfRangeCursor = SearchCursorCodec.Encode(new
        {
            Query = "tea",
            RelevanceRank = 0,
            NormalizedLabel = "tea",
            TypeOrder = short.MaxValue + 1,
            StableId = "00000000-0000-0000-0000-000000000401"
        });

        await Assert.ThrowsAsync<ValidationException>(
            () => search.SearchAsync(new DiscoverySearchRequest("tea", outOfRangeCursor, 1)));
    }

    private static SearchDocument Document(
        string type,
        string title,
        string normalizedText,
        bool isDeleted = false,
        short? typeOrder = null,
        string? entityId = null) => new()
        {
            EntityType = type,
            EntityId = entityId ?? Guid.NewGuid().ToString(),
            Title = title,
            Subtitle = type,
            Route = "/search",
            NormalizedText = normalizedText,
            TypeOrder = typeOrder ?? SearchDocumentTypes.GetTypeOrder(type),
            SourceVersion = 1,
            IsDeleted = isDeleted,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private sealed class SearchSqlCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

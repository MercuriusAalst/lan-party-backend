using Mercurius.Modules.Discovery.Application;
using Mercurius.Modules.Discovery.Contracts;
using Mercurius.Modules.Discovery.Domain;
using Mercurius.Modules.Discovery.Infrastructure;
using Mercurius.Modules.Shared.Search;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Discovery;

internal sealed class DiscoveryModuleFacade : IDiscoveryModule
{
    private readonly IDiscoveryDbContext _dbContext;
    private readonly SearchIndexRebuildService _rebuildService;

    public DiscoveryModuleFacade(
        IDiscoveryDbContext dbContext,
        SearchIndexRebuildService rebuildService)
    {
        _dbContext = dbContext;
        _rebuildService = rebuildService;
    }

    public async Task<DiscoverySearchResponse> SearchAsync(
        DiscoverySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = SearchRequest.NormalizeQuery(request.Query);
        SearchRequest.ValidateQueryLength(normalizedQuery);

        var pageSize = SearchRequest.BoundPageSize(request.PageSize);
        if (normalizedQuery.Length < SearchRequestLimits.MinimumQueryLength)
            return new DiscoverySearchResponse([], null, false);

        var cursor = DecodeCursor(request.Cursor, normalizedQuery);
        var candidates = await GetPagedCandidatesAsync(
            normalizedQuery,
            cursor,
            pageSize + 1,
            cancellationToken);

        var hasMore = candidates.Count > pageSize;
        if (hasMore)
            candidates.RemoveAt(candidates.Count - 1);

        return new DiscoverySearchResponse(
            candidates.Select(ToResult).ToList(),
            hasMore ? BuildCursor(normalizedQuery, candidates[^1]) : null,
            hasMore);
    }

    public Task<DiscoverySearchIndexRebuildJob> CreateSearchIndexRebuildJobAsync(
        CancellationToken cancellationToken = default) =>
        _rebuildService.CreateJobAsync(cancellationToken);

    public Task<DiscoverySearchIndexRebuildJob?> GetSearchIndexRebuildJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        _rebuildService.GetJobAsync(jobId, cancellationToken);

    private async Task<List<SearchCandidate>> GetPagedCandidatesAsync(
        string normalizedQuery,
        SearchCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var escapedQuery = SearchRequest.EscapeLikePattern(normalizedQuery);
        var containsPattern = $"%{escapedQuery}%";
        var prefixPattern = $"{escapedQuery}%";
        var candidates = new List<SearchCandidate>(limit);
        var firstRank = cursor?.RelevanceRank ?? 0;

        for (var rank = firstRank; rank <= 2 && candidates.Count < limit; rank++)
        {
            var rankCursor = cursor is not null && rank == cursor.RelevanceRank
                ? cursor
                : null;
            var rankCandidates = BuildRankCandidateQuery(
                normalizedQuery,
                prefixPattern,
                containsPattern,
                rank,
                rankCursor,
                limit - candidates.Count);

            candidates.AddRange(await rankCandidates.ToListAsync(cancellationToken));
        }

        return candidates;
    }

    private IQueryable<SearchCandidate> BuildRankCandidateQuery(
        string normalizedQuery,
        string prefixPattern,
        string containsPattern,
        int rank,
        SearchCursor? cursor,
        int limit)
    {
        IQueryable<SearchDocument> documents = rank == 1
            ? CreatePrefixSource(normalizedQuery)
            : _dbContext.SearchDocuments;

        documents = documents
            .AsNoTracking()
            .Where(document =>
                !document.IsDeleted &&
                (document.EntityType == SearchDocumentTypes.User ||
                 document.EntityType == SearchDocumentTypes.Team ||
                 document.EntityType == SearchDocumentTypes.Tournament));

        documents = rank switch
        {
            0 => documents.Where(document => document.NormalizedText == normalizedQuery),
            1 => documents.Where(document =>
                EF.Functions.Like(document.NormalizedText, prefixPattern, "\\") &&
                document.NormalizedText != normalizedQuery),
            2 => documents.Where(document =>
                EF.Functions.Like(document.NormalizedText, containsPattern, "\\") &&
                !EF.Functions.Like(document.NormalizedText, prefixPattern, "\\")),
            _ => throw new ArgumentOutOfRangeException(nameof(rank))
        };

        if (cursor is not null)
        {
            documents = documents.Where(document =>
                EF.Functions.GreaterThan(
                    ValueTuple.Create(document.NormalizedText, document.TypeOrder, document.EntityId),
                    ValueTuple.Create(cursor.NormalizedLabel, (short)cursor.TypeOrder, cursor.StableId)));
        }

        return documents
            .OrderBy(document => document.NormalizedText)
            .ThenBy(document => document.TypeOrder)
            .ThenBy(document => document.EntityId)
            .Take(limit)
            .Select(document => new SearchCandidate
            {
                RelevanceRank = rank,
                NormalizedLabel = document.NormalizedText,
                TypeOrder = document.TypeOrder,
                StableId = document.EntityId,
                Type = document.EntityType,
                DisplayLabel = document.Title,
                ImageUrl = document.ImageUrl
            });
    }

    private IQueryable<SearchDocument> CreatePrefixSource(string normalizedQuery)
    {
        var upper = SearchRequest.GetPrefixUpperBound(normalizedQuery);

        return upper is null
            ? _dbContext.SearchDocuments.FromSqlInterpolated($"""
                SELECT * FROM discovery.search_documents
                WHERE is_deleted = false
                  AND normalized_text >= {normalizedQuery}
                """)
            : _dbContext.SearchDocuments.FromSqlInterpolated($"""
                SELECT * FROM discovery.search_documents
                WHERE is_deleted = false
                  AND normalized_text >= {normalizedQuery}
                  AND normalized_text < {upper}
                """);
    }

    private static DiscoverySearchResult ToResult(SearchCandidate candidate)
    {
        return candidate.Type switch
        {
            SearchDocumentTypes.User => new DiscoverySearchResult(
                candidate.Type,
                candidate.DisplayLabel,
                "User",
                candidate.DisplayLabel,
                null,
                null,
                candidate.ImageUrl),
            SearchDocumentTypes.Team => new DiscoverySearchResult(
                candidate.Type,
                candidate.DisplayLabel,
                "Team",
                null,
                candidate.DisplayLabel,
                null),
            SearchDocumentTypes.Tournament when Guid.TryParse(candidate.StableId, out var tournamentId) => new DiscoverySearchResult(
                candidate.Type,
                candidate.DisplayLabel,
                "Tournament",
                null,
                null,
                tournamentId),
            _ => throw new InvalidOperationException($"Unsupported search document type '{candidate.Type}'.")
        };
    }

    private static string BuildCursor(string normalizedQuery, SearchCandidate candidate)
    {
        return SearchCursorCodec.Encode(new SearchCursor(
            normalizedQuery,
            candidate.RelevanceRank,
            candidate.NormalizedLabel,
            candidate.TypeOrder,
            candidate.StableId));
    }

    private static SearchCursor? DecodeCursor(string? cursor, string normalizedQuery)
    {
        return SearchCursorCodec.Decode<SearchCursor>(
            cursor,
            normalizedQuery,
            payload =>
                !string.IsNullOrEmpty(payload.Query) &&
                payload.RelevanceRank is >= 0 and <= 2 &&
                !string.IsNullOrEmpty(payload.NormalizedLabel) &&
                payload.TypeOrder is >= short.MinValue and <= short.MaxValue &&
                Guid.TryParse(payload.StableId, out _),
            payload => payload.Query);
    }

    private sealed class SearchCandidate
    {
        public int RelevanceRank { get; init; }
        public required string NormalizedLabel { get; init; }
        public short TypeOrder { get; init; }
        public required string StableId { get; init; }
        public required string Type { get; init; }
        public required string DisplayLabel { get; init; }
        public string? ImageUrl { get; init; }
    }

    private sealed record SearchCursor(
        string Query,
        int RelevanceRank,
        string NormalizedLabel,
        int TypeOrder,
        string StableId);
}

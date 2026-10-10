using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Discovery.Contracts;
using Mercurius.Modules.Discovery.Domain;
using Mercurius.Modules.Discovery.Infrastructure;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mercurius.Modules.Discovery.Application;

internal sealed class SearchIndexRebuildService
{
    private const string FailureMessage = "The rebuild failed. Check server logs for details.";
    private const int RebuildPageSize = 1000;
    private readonly IDiscoveryDbContext _dbContext;
    private readonly DiscoveryRebuildOwnership _ownership;
    private readonly IIdentityModule _identityModule;
    private readonly ITeamsModule _teamsModule;
    private readonly ITournamentModule _tournamentModule;
    private readonly ISponsorshipModule _sponsorshipModule;
    private readonly ILogger<SearchIndexRebuildService> _logger;
    private readonly TimeProvider _timeProvider;

    public SearchIndexRebuildService(
        IDiscoveryDbContext dbContext,
        DiscoveryRebuildOwnership ownership,
        IIdentityModule identityModule,
        ITeamsModule teamsModule,
        ITournamentModule tournamentModule,
        ISponsorshipModule sponsorshipModule,
        ILogger<SearchIndexRebuildService> logger,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _ownership = ownership;
        _identityModule = identityModule;
        _teamsModule = teamsModule;
        _tournamentModule = tournamentModule;
        _sponsorshipModule = sponsorshipModule;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<DiscoverySearchIndexRebuildJob> CreateJobAsync(CancellationToken cancellationToken)
    {
        var activeJob = await _dbContext.SearchIndexRebuildJobs
            .AsNoTracking()
            .Where(job => job.Status == SearchIndexRebuildJobStatus.Pending || job.Status == SearchIndexRebuildJobStatus.Running)
            .OrderBy(job => job.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (activeJob is not null)
            return ToContract(activeJob);

        var job = new SearchIndexRebuildJob
        {
            CreatedAtUtc = UtcNow()
        };
        _dbContext.SearchIndexRebuildJobs.Add(job);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return ToContract(job);
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(job).State = EntityState.Detached;
            var existingJob = await _dbContext.SearchIndexRebuildJobs
                .AsNoTracking()
                .Where(candidate => candidate.Status == SearchIndexRebuildJobStatus.Pending || candidate.Status == SearchIndexRebuildJobStatus.Running)
                .OrderBy(candidate => candidate.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (existingJob is not null)
                return ToContract(existingJob);

            throw;
        }
    }

    public async Task EnsureInitialJobAsync(CancellationToken cancellationToken)
    {
        await _ownership.EnsureOwnedAsync(cancellationToken);
        var hasProjectedDocuments = await _dbContext.SearchDocuments
            .AsNoTracking()
            .AnyAsync(document => !document.IsDeleted, cancellationToken);
        if (hasProjectedDocuments)
            return;

        var hasCompletedJob = await _dbContext.SearchIndexRebuildJobs
            .AsNoTracking()
            .AnyAsync(job => job.Status == SearchIndexRebuildJobStatus.Completed, cancellationToken);
        if (hasCompletedJob)
            return;

        _ = await CreateJobAsync(cancellationToken);
    }

    public async Task<DiscoverySearchIndexRebuildJob?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await _dbContext.SearchIndexRebuildJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        return job is null ? null : ToContract(job);
    }

    public async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        await _ownership.EnsureOwnedAsync(cancellationToken);
        var job = await _dbContext.SearchIndexRebuildJobs
            .Where(candidate => candidate.Status == SearchIndexRebuildJobStatus.Pending)
            .OrderBy(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
            return false;

        job.Status = SearchIndexRebuildJobStatus.Running;
        job.StartedAtUtc = UtcNow();
        job.Error = null;
        await _ownership.EnsureOwnedAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var sourceVersion = job.StartedAtUtc.Value.Ticks;
            await RebuildDocumentsAsync(job, sourceVersion, job.StartedAtUtc.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _ownership.EnsureOwnedAsync(CancellationToken.None);
            _logger.LogError(exception, "Discovery search-index rebuild job {JobId} failed.", job.Id);
            try
            {
                await ClearStagedDocumentsAsync(job.Id, cancellationToken);
            }
            catch (Exception cleanupException)
            {
                await _ownership.EnsureOwnedAsync(CancellationToken.None);
                _logger.LogError(cleanupException, "Discovery search-index rebuild cleanup failed for job {JobId}.", job.Id);
            }

            job.Status = SearchIndexRebuildJobStatus.Failed;
            job.CompletedAtUtc = UtcNow();
            job.Error = FailureMessage;
            await _ownership.EnsureOwnedAsync(CancellationToken.None);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await _ownership.EnsureOwnedAsync(cancellationToken);
        var interruptedJobs = await _dbContext.SearchIndexRebuildJobs
            .Where(job =>
                job.Status == SearchIndexRebuildJobStatus.Running)
            .ToListAsync(cancellationToken);
        if (interruptedJobs.Count == 0)
            return;

        foreach (var job in interruptedJobs)
        {
            await ClearStagedDocumentsAsync(job.Id, cancellationToken);
            job.Status = SearchIndexRebuildJobStatus.Pending;
            job.StartedAtUtc = null;
            job.CompletedAtUtc = null;
            job.Error = null;
        }

        await _ownership.EnsureOwnedAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "Requeued {Count} interrupted Discovery search-index rebuild job(s) before rebuild work.",
            interruptedJobs.Count);
    }

    private async Task RebuildDocumentsAsync(
        SearchIndexRebuildJob job,
        long sourceVersion,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await StageUserDocumentsAsync(job, sourceVersion, updatedAtUtc, cancellationToken);
        await StageTeamDocumentsAsync(job, sourceVersion, updatedAtUtc, cancellationToken);
        await StageTournamentDocumentsAsync(job, sourceVersion, updatedAtUtc, cancellationToken);
        await StageSponsorDocumentsAsync(job, sourceVersion, updatedAtUtc, cancellationToken);

        if (_dbContext.IsRelational)
            await MergeStagedDocumentsRelationalAsync(job, sourceVersion, updatedAtUtc, cancellationToken);
        else
            await MergeStagedDocumentsInMemoryAsync(job, sourceVersion, updatedAtUtc, cancellationToken);
    }

    private async Task StageUserDocumentsAsync(
        SearchIndexRebuildJob job,
        long sourceVersion,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        UserId? afterId = null;
        while (true)
        {
            var users = await _identityModule.GetPublicUserSearchDocumentsPageAsync(afterId, RebuildPageSize, cancellationToken);
            if (users.Count == 0)
                return;

            await StageDocumentsAsync(
                job,
                SearchDocumentTypes.User,
                users.Select(user => new SearchDocumentProjection(
                    user.UserId.Value.ToString(),
                    user.Username,
                    "User",
                    ImageUrl: null,
                    $"/users/{Uri.EscapeDataString(user.Username)}")).ToArray(),
                sourceVersion,
                updatedAtUtc,
                cancellationToken);

            afterId = users[^1].UserId;
            if (users.Count < RebuildPageSize)
                return;
        }
    }

    private async Task StageTeamDocumentsAsync(
        SearchIndexRebuildJob job,
        long sourceVersion,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        TeamId? afterId = null;
        while (true)
        {
            var teams = await _teamsModule.GetPublicTeamSearchDocumentsPageAsync(afterId, RebuildPageSize, cancellationToken);
            if (teams.Count == 0)
                return;

            await StageDocumentsAsync(
                job,
                SearchDocumentTypes.Team,
                teams.Select(team => new SearchDocumentProjection(
                    team.TeamId.Value.ToString(),
                    team.Name,
                    "Team",
                    ImageUrl: null,
                    $"/teams/{Uri.EscapeDataString(team.Name)}")).ToArray(),
                sourceVersion,
                updatedAtUtc,
                cancellationToken);

            afterId = teams[^1].TeamId;
            if (teams.Count < RebuildPageSize)
                return;
        }
    }

    private async Task StageTournamentDocumentsAsync(
        SearchIndexRebuildJob job,
        long sourceVersion,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        TournamentId? afterId = null;
        while (true)
        {
            var tournaments = await _tournamentModule.GetTournamentSearchDocumentsPageAsync(afterId, RebuildPageSize, cancellationToken);
            if (tournaments.Count == 0)
                return;

            await StageDocumentsAsync(
                job,
                SearchDocumentTypes.Tournament,
                tournaments.Select(tournament => new SearchDocumentProjection(
                    tournament.TournamentId.Value.ToString(),
                    tournament.Name,
                    "Tournament",
                    tournament.ImageUrl,
                    $"/tournaments/{tournament.TournamentId.Value}")).ToArray(),
                sourceVersion,
                updatedAtUtc,
                cancellationToken);

            afterId = tournaments[^1].TournamentId;
            if (tournaments.Count < RebuildPageSize)
                return;
        }
    }

    private async Task StageSponsorDocumentsAsync(
        SearchIndexRebuildJob job,
        long sourceVersion,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        SponsorId? afterId = null;
        while (true)
        {
            var sponsors = await _sponsorshipModule.GetSponsorSearchDocumentsPageAsync(afterId, RebuildPageSize, cancellationToken);
            if (sponsors.Count == 0)
                return;

            await StageDocumentsAsync(
                job,
                SearchDocumentTypes.Sponsor,
                sponsors.Select(sponsor => new SearchDocumentProjection(
                    sponsor.SponsorId.Value.ToString(),
                    sponsor.Name,
                    "Sponsor",
                    sponsor.LogoUrl,
                    $"/sponsors/{sponsor.SponsorId.Value}")).ToArray(),
                sourceVersion,
                updatedAtUtc,
                cancellationToken);

            afterId = sponsors[^1].SponsorId;
            if (sponsors.Count < RebuildPageSize)
                return;
        }
    }

    private async Task StageDocumentsAsync(
        SearchIndexRebuildJob job,
        string entityType,
        IReadOnlyCollection<SearchDocumentProjection> projections,
        long sourceVersion,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var stagedDocuments = projections.Select(projection => new SearchIndexRebuildDocument
        {
            JobId = job.Id,
            EntityType = entityType,
            EntityId = projection.EntityId,
            TypeOrder = SearchDocumentTypes.GetTypeOrder(entityType),
            Title = projection.Title,
            Subtitle = projection.Subtitle,
            ImageUrl = projection.ImageUrl,
            Route = projection.Route,
            NormalizedText = projection.Title.Trim().ToLowerInvariant(),
            SourceVersion = sourceVersion,
            UpdatedAtUtc = updatedAtUtc
        }).ToList();

        _dbContext.SearchIndexRebuildDocuments.AddRange(stagedDocuments);
        await _ownership.EnsureOwnedAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var document in stagedDocuments)
            _dbContext.Entry(document).State = EntityState.Detached;
    }

    private async Task MergeStagedDocumentsRelationalAsync(
        SearchIndexRebuildJob job,
        long sourceVersion,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await _ownership.EnsureOwnedAsync(cancellationToken);
        await using var transaction = await _dbContext.BeginTransactionAsync(cancellationToken);

        await _ownership.EnsureOwnedAsync(cancellationToken);
        await _dbContext.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO discovery.search_documents (
                id, entity_type, entity_id, title, subtitle, image_url, route, normalized_text,
                type_order, source_version, is_deleted, updated_at_utc)
            SELECT id, entity_type, entity_id, title, subtitle, image_url, route, normalized_text,
                type_order, source_version, false, updated_at_utc
            FROM discovery.search_index_rebuild_documents
            WHERE job_id = {job.Id}
            ON CONFLICT (entity_type, entity_id) DO UPDATE SET
                title = EXCLUDED.title,
                subtitle = EXCLUDED.subtitle,
                image_url = EXCLUDED.image_url,
                route = EXCLUDED.route,
                normalized_text = EXCLUDED.normalized_text,
                type_order = EXCLUDED.type_order,
                source_version = EXCLUDED.source_version,
                is_deleted = false,
                updated_at_utc = EXCLUDED.updated_at_utc
            WHERE discovery.search_documents.source_version <= EXCLUDED.source_version;
            """, cancellationToken);

        await _ownership.EnsureOwnedAsync(cancellationToken);
        await _dbContext.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery.search_documents AS document
            SET title = '', subtitle = '', image_url = NULL, route = '', normalized_text = '',
                source_version = {sourceVersion}, is_deleted = true, updated_at_utc = {updatedAtUtc}
            WHERE document.is_deleted = false
              AND document.source_version <= {sourceVersion}
              AND NOT EXISTS (
                  SELECT 1
                  FROM discovery.search_index_rebuild_documents AS staged
                  WHERE staged.job_id = {job.Id}
                    AND staged.entity_type = document.entity_type
                    AND staged.entity_id = document.entity_id);
            """, cancellationToken);

        await ClearStagedDocumentsAsync(job.Id, cancellationToken);
        job.Status = SearchIndexRebuildJobStatus.Completed;
        job.CompletedAtUtc = UtcNow();
        await _ownership.EnsureOwnedAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _ownership.EnsureOwnedAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MergeStagedDocumentsInMemoryAsync(
        SearchIndexRebuildJob job,
        long sourceVersion,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await _ownership.EnsureOwnedAsync(cancellationToken);
        var stagedDocuments = await _dbContext.SearchIndexRebuildDocuments
            .Where(document => document.JobId == job.Id)
            .ToListAsync(cancellationToken);
        var stagedKeys = stagedDocuments
            .Select(document => (document.EntityType, document.EntityId))
            .ToHashSet();
        var existingDocuments = await _dbContext.SearchDocuments.ToListAsync(cancellationToken);
        var documentsByKey = existingDocuments.ToDictionary(document => (document.EntityType, document.EntityId));

        foreach (var staged in stagedDocuments)
        {
            if (!documentsByKey.TryGetValue((staged.EntityType, staged.EntityId), out var document))
            {
                document = new SearchDocument
                {
                    EntityType = staged.EntityType,
                    EntityId = staged.EntityId
                };
                _dbContext.SearchDocuments.Add(document);
            }
            else if (document.SourceVersion > staged.SourceVersion)
            {
                continue;
            }

            document.Title = staged.Title;
            document.Subtitle = staged.Subtitle;
            document.ImageUrl = staged.ImageUrl;
            document.Route = staged.Route;
            document.NormalizedText = staged.NormalizedText;
            document.TypeOrder = staged.TypeOrder;
            document.SourceVersion = staged.SourceVersion;
            document.IsDeleted = false;
            document.UpdatedAtUtc = staged.UpdatedAtUtc;
        }

        foreach (var document in existingDocuments.Where(document =>
                     !document.IsDeleted &&
                     document.SourceVersion <= sourceVersion &&
                     !stagedKeys.Contains((document.EntityType, document.EntityId))))
        {
            document.Title = string.Empty;
            document.Subtitle = string.Empty;
            document.ImageUrl = null;
            document.Route = string.Empty;
            document.NormalizedText = string.Empty;
            document.SourceVersion = sourceVersion;
            document.IsDeleted = true;
            document.UpdatedAtUtc = updatedAtUtc;
        }

        _dbContext.SearchIndexRebuildDocuments.RemoveRange(stagedDocuments);
        job.Status = SearchIndexRebuildJobStatus.Completed;
        job.CompletedAtUtc = UtcNow();
        await _ownership.EnsureOwnedAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ClearStagedDocumentsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await _ownership.EnsureOwnedAsync(cancellationToken);
        if (_dbContext.IsRelational)
        {
            await _dbContext.ExecuteSqlInterpolatedAsync($"""
                TRUNCATE TABLE discovery.search_index_rebuild_documents;
                """, cancellationToken);
            return;
        }

        var stagedDocuments = await _dbContext.SearchIndexRebuildDocuments
            .Where(document => document.JobId == jobId)
            .ToListAsync(cancellationToken);
        _dbContext.SearchIndexRebuildDocuments.RemoveRange(stagedDocuments);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static DiscoverySearchIndexRebuildJob ToContract(SearchIndexRebuildJob job)
    {
        return new DiscoverySearchIndexRebuildJob(
            job.Id,
            job.Status.ToString().ToLowerInvariant(),
            job.CreatedAtUtc,
            job.StartedAtUtc,
            job.CompletedAtUtc,
            job.Error);
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
}

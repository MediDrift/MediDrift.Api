using System.Text.Json;
using MediDrift.Api.Data;
using MediDrift.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace MediDrift.Api.Services;

/// <summary>
/// Processes queued catalogue audits. Lookups are deduplicated within a run and
/// cached for 24 hours so repeated identifiers do not repeatedly call RxNav.
/// Source medication records are never changed by this service.
/// </summary>
public sealed class MedicationAuditService(
    AppDbContext db,
    RxNormClient rxNormClient,
    ILogger<MedicationAuditService> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(250);
    private const int ProgressSaveBatchSize = 5;
    private const int FindingsSaveBatchSize = 500;

    public async Task ProcessRunAsync(Guid auditRunId, CancellationToken cancellationToken)
    {
        var run = await db.MedicationAuditRuns
            .FirstOrDefaultAsync(x => x.Id == auditRunId, cancellationToken);
        if (run is null || run.Status is MedicationAuditStatuses.Completed
            or MedicationAuditStatuses.Failed or MedicationAuditStatuses.Pausing
            or MedicationAuditStatuses.Paused or MedicationAuditStatuses.Cancelling
            or MedicationAuditStatuses.Cancelled)
            return;

        // A previously started run is being resumed after a pause or process restart.
        // Keep completed findings and counters; cached terminology lookups avoid
        // repeating external requests for work that has already finished.
        var isResume = run.StartedUtc is not null;
        var startedUtc = DateTime.UtcNow;

        // Atomically claim only a queued run. If a user pauses/cancels between
        // worker selection and execution, the changed state wins and no work starts.
        var claimed = await db.MedicationAuditRuns
            .Where(x => x.Id == run.Id && x.Status == MedicationAuditStatuses.Queued)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, MedicationAuditStatuses.Running)
                .SetProperty(x => x.StartedUtc, x => x.StartedUtc ?? startedUtc)
                .SetProperty(x => x.CompletedUtc, (DateTime?)null)
                .SetProperty(x => x.ErrorMessage, (string?)null), cancellationToken);

        if (claimed == 0)
            return;

        await db.Entry(run).ReloadAsync(cancellationToken);
        if (run.Status is MedicationAuditStatuses.Paused or MedicationAuditStatuses.Cancelled)
            return;

        if (!isResume)
        {
            run.ProcessedIdentifiers = 0;
            run.ActiveCount = 0;
            run.ObsoleteCount = 0;
            run.RemappedCount = 0;
            run.QuantifiedCount = 0;
            run.NotCurrentCount = 0;
            run.UnknownCount = 0;
            run.AlienCount = 0;
            run.InvalidFormatCount = 0;
            run.MissingIdentifierCount = 0;
            run.LookupErrorCount = 0;
        }

        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var records = await db.MedicationRecords.AsNoTracking()
                .Where(x => x.ImportJobId == run.ImportJobId)
                .OrderBy(x => x.SourceRowNumber)
                .ToListAsync(cancellationToken);

            try
            {
                run.RxNormVersion = await rxNormClient.GetRxNormVersionAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not retrieve the current RxNorm release version.");
                run.RxNormVersion = null;
            }

            var rowChecks = new List<RowIdentifierCheck>();
            var distinctChecks = new Dictionary<string, IdentifierCheck>(StringComparer.Ordinal);

            foreach (var record in records)
            {
                var hasIdentifier = false;
                if (!string.IsNullOrWhiteSpace(record.RxCUI))
                {
                    hasIdentifier = true;
                    AddIdentifierCheck(record, "RxCUI", record.RxCUI!, rowChecks, distinctChecks);
                }
                if (!string.IsNullOrWhiteSpace(record.NDC))
                {
                    hasIdentifier = true;
                    AddIdentifierCheck(record, "NDC", record.NDC!, rowChecks, distinctChecks);
                }
                if (!hasIdentifier)
                {
                    rowChecks.Add(RowIdentifierCheck.Missing(record));
                }
            }

            run.TotalRecords = records.Count;
            var missingIdentifierRows = rowChecks.Count(x => x.Type == "None");
            run.TotalIdentifiers = distinctChecks.Count + missingIdentifierRows;
            if (!isResume)
                run.ProcessedIdentifiers = missingIdentifierRows;
            await db.SaveChangesAsync(cancellationToken);

            var canContinue = await ResolveIdentifiersAsync(run, distinctChecks, rowChecks, cancellationToken);
            if (!canContinue)
                return;

            var findings = await db.MedicationAuditFindings.AsNoTracking()
                .Where(x => x.AuditRunId == run.Id)
                .ToListAsync(cancellationToken);

            run.ActiveCount = findings.Count(x => x.Status == FindingStatuses.Active);
            run.ObsoleteCount = findings.Count(x => x.Status == FindingStatuses.Obsolete);
            run.RemappedCount = findings.Count(x => x.Status == FindingStatuses.Remapped);
            run.QuantifiedCount = findings.Count(x => x.Status == FindingStatuses.Quantified);
            run.NotCurrentCount = findings.Count(x => x.Status == FindingStatuses.NotCurrent);
            run.UnknownCount = findings.Count(x => x.Status == FindingStatuses.Unknown);
            run.AlienCount = findings.Count(x => x.Status == FindingStatuses.Alien);
            run.InvalidFormatCount = findings.Count(x => x.Status == FindingStatuses.InvalidFormat);
            run.MissingIdentifierCount = findings.Count(x => x.Status == FindingStatuses.MissingIdentifier);
            run.LookupErrorCount = findings.Count(x => x.Status == FindingStatuses.LookupError);

            await db.SaveChangesAsync(cancellationToken);

            // Complete only when the persisted state is still Running. A concurrent
            // pause or cancel request wins over completion.
            var completedUtc = DateTime.UtcNow;
            await db.MedicationAuditRuns
                .Where(x => x.Id == run.Id && x.Status == MedicationAuditStatuses.Running)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, MedicationAuditStatuses.Completed)
                    .SetProperty(x => x.CompletedUtc, completedUtc), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker will re-queue a still-running audit after the next process start.
            throw;
        }
        catch (Exception exception)
        {
            var currentStatus = await db.MedicationAuditRuns.AsNoTracking()
                .Where(x => x.Id == run.Id)
                .Select(x => x.Status)
                .FirstOrDefaultAsync(CancellationToken.None);

            if (currentStatus == MedicationAuditStatuses.Pausing)
            {
                await db.MedicationAuditRuns
                    .Where(x => x.Id == run.Id && x.Status == MedicationAuditStatuses.Pausing)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, MedicationAuditStatuses.Paused), CancellationToken.None);
                return;
            }

            if (currentStatus == MedicationAuditStatuses.Cancelling)
            {
                var cancelledUtc = DateTime.UtcNow;
                await db.MedicationAuditRuns
                    .Where(x => x.Id == run.Id && x.Status == MedicationAuditStatuses.Cancelling)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, MedicationAuditStatuses.Cancelled)
                        .SetProperty(x => x.CompletedUtc, cancelledUtc)
                        .SetProperty(x => x.ErrorMessage, "Cancelled by the user. Partial findings have been retained."), CancellationToken.None);
                return;
            }

            if (currentStatus is MedicationAuditStatuses.Paused or MedicationAuditStatuses.Cancelled)
                return;

            var failedUtc = DateTime.UtcNow;
            var safeMessage = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
            await db.MedicationAuditRuns
                .Where(x => x.Id == run.Id && x.Status == MedicationAuditStatuses.Running)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, MedicationAuditStatuses.Failed)
                    .SetProperty(x => x.ErrorMessage, safeMessage)
                    .SetProperty(x => x.CompletedUtc, failedUtc), CancellationToken.None);
            logger.LogError(exception, "Medication audit {AuditRunId} failed.", auditRunId);
        }
    }

    private async Task<bool> ResolveIdentifiersAsync(
        MedicationAuditRun run,
        IReadOnlyDictionary<string, IdentifierCheck> checks,
        IReadOnlyList<RowIdentifierCheck> rowChecks,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, RxNormLookupResult>(StringComparer.Ordinal);
        var rowsByIdentifier = rowChecks
            .Where(x => x.Type != "None")
            .GroupBy(x => MakeKey(x.Type, x.NormalizedIdentifier))
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var existingFindingKeys = await db.MedicationAuditFindings.AsNoTracking()
            .Where(x => x.AuditRunId == run.Id)
            .Select(x => new { x.MedicationRecordId, x.IdentifierType })
            .ToListAsync(cancellationToken);
        var persistedFindingKeys = existingFindingKeys
            .Select(x => $"{x.MedicationRecordId}:{x.IdentifierType}")
            .ToHashSet(StringComparer.Ordinal);
        var resolvedKeys = new List<string>();
        var nextResolvedKey = 0;
        var pendingProgressWrites = 0;

        async Task PersistAvailableFindingsAsync()
        {
            var pendingFindings = new List<MedicationAuditFinding>();

            // Missing identifier findings are saved once and retained across resumes.
            foreach (var missingCheck in rowChecks.Where(x => x.Type == "None"))
            {
                var findingKey = $"{missingCheck.Record.Id}:None";
                if (persistedFindingKeys.Add(findingKey))
                    pendingFindings.Add(CreateFinding(run.Id, missingCheck, results));
            }

            // Persist the findings for each newly resolved unique identifier. Rows
            // sharing an identifier all receive a finding, while only one RxNorm
            // request is made for that identifier.
            while (nextResolvedKey < resolvedKeys.Count)
            {
                var key = resolvedKeys[nextResolvedKey++];
                if (!rowsByIdentifier.TryGetValue(key, out var matchingRows))
                    continue;

                foreach (var rowCheck in matchingRows)
                {
                    var findingKey = $"{rowCheck.Record.Id}:{rowCheck.Type}";
                    if (persistedFindingKeys.Add(findingKey))
                        pendingFindings.Add(CreateFinding(run.Id, rowCheck, results));
                }
            }

            foreach (var batch in pendingFindings.Chunk(FindingsSaveBatchSize))
            {
                db.MedicationAuditFindings.AddRange(batch);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        async Task<bool> ContinueOrFinalizeControlRequestAsync()
        {
            var currentStatus = await db.MedicationAuditRuns.AsNoTracking()
                .Where(x => x.Id == run.Id)
                .Select(x => x.Status)
                .FirstOrDefaultAsync(cancellationToken);

            if (currentStatus == MedicationAuditStatuses.Pausing)
            {
                await db.MedicationAuditRuns
                    .Where(x => x.Id == run.Id && x.Status == MedicationAuditStatuses.Pausing)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, MedicationAuditStatuses.Paused), cancellationToken);
                return false;
            }

            if (currentStatus == MedicationAuditStatuses.Cancelling)
            {
                var cancelledUtc = DateTime.UtcNow;
                await db.MedicationAuditRuns
                    .Where(x => x.Id == run.Id && x.Status == MedicationAuditStatuses.Cancelling)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, MedicationAuditStatuses.Cancelled)
                        .SetProperty(x => x.CompletedUtc, cancelledUtc)
                        .SetProperty(x => x.ErrorMessage, "Cancelled by the user. Partial findings have been retained."), cancellationToken);
                return false;
            }

            return currentStatus is not (MedicationAuditStatuses.Paused or MedicationAuditStatuses.Cancelled);
        }

        async Task<bool> FlushProgressAsync(bool force = false)
        {
            if (!force && pendingProgressWrites < ProgressSaveBatchSize)
                return true;

            // Save the final in-flight batch before honouring a pause/cancel request.
            await PersistAvailableFindingsAsync();
            await db.SaveChangesAsync(cancellationToken);
            pendingProgressWrites = 0;

            return await ContinueOrFinalizeControlRequestAsync();
        }

        if (checks.Count == 0)
        {
            await PersistAvailableFindingsAsync();
            await db.SaveChangesAsync(cancellationToken);
            return await ContinueOrFinalizeControlRequestAsync();
        }

        var checksToResolve = checks
            .Where(pair => !rowsByIdentifier.TryGetValue(pair.Key, out var matchingRows)
                || matchingRows.Any(row => !persistedFindingKeys.Contains($"{row.Record.Id}:{row.Type}")))
            .ToArray();

        if (checksToResolve.Length == 0)
        {
            await PersistAvailableFindingsAsync();
            await db.SaveChangesAsync(cancellationToken);
            return await ContinueOrFinalizeControlRequestAsync();
        }

        var typesAndIds = checksToResolve
            .Select(x => (x.Value.Type, x.Value.NormalizedIdentifier))
            .Distinct()
            .ToArray();
        var cacheTypes = typesAndIds.Select(x => x.Type).Distinct().ToArray();
        var cacheIds = typesAndIds.Select(x => x.NormalizedIdentifier).Distinct().ToArray();
        var cacheEntries = await db.TerminologyCacheEntries
            .Where(x => cacheTypes.Contains(x.IdentifierType) && cacheIds.Contains(x.Identifier))
            .ToListAsync(cancellationToken);
        var cacheByKey = cacheEntries.ToDictionary(CacheKey, StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        foreach (var (key, check) in checksToResolve)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (check.InvalidFormatMessage is not null)
            {
                results[key] = RxNormResponseParser.InvalidFormat(check.Type, check.SubmittedIdentifier, check.InvalidFormatMessage);
                resolvedKeys.Add(key);
                run.ProcessedIdentifiers++;
                pendingProgressWrites++;
                if (!await FlushProgressAsync())
                    return false;
                continue;
            }

            if (cacheByKey.TryGetValue(key, out var cached)
                && cached.ExpiresUtc > now
                && !string.IsNullOrWhiteSpace(run.RxNormVersion)
                && string.Equals(cached.RxNormVersion, run.RxNormVersion, StringComparison.Ordinal))
            {
                try
                {
                    results[key] = check.Type == "RxCUI"
                        ? RxNormResponseParser.ParseRxcui(cached.RawResponseJson, true)
                        : RxNormResponseParser.ParseNdc(cached.RawResponseJson, true);
                    resolvedKeys.Add(key);
                    run.ProcessedIdentifiers++;
                    pendingProgressWrites++;
                    if (!await FlushProgressAsync())
                        return false;
                    continue;
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Discarding unreadable terminology cache entry {CacheKey}.", key);
                }
            }

            try
            {
                var rawJson = check.Type == "RxCUI"
                    ? await rxNormClient.GetRxcuiHistoryStatusAsync(check.NormalizedIdentifier, cancellationToken)
                    : await rxNormClient.GetNdcStatusAsync(check.NormalizedIdentifier, cancellationToken);
                var parsed = check.Type == "RxCUI"
                    ? RxNormResponseParser.ParseRxcui(rawJson)
                    : RxNormResponseParser.ParseNdc(rawJson);
                results[key] = parsed;
                SaveCache(cacheByKey, key, check, parsed, rawJson, run.RxNormVersion, now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Terminology lookup failed for {Type} {Identifier}.", check.Type, check.NormalizedIdentifier);
                var safeDetail = exception is HttpRequestException
                    ? "RxNav returned an HTTP or network error."
                    : "RxNav returned an unreadable response or the request timed out.";
                results[key] = RxNormResponseParser.ServiceError(check.Type, safeDetail);
            }

            resolvedKeys.Add(key);
            run.ProcessedIdentifiers++;
            pendingProgressWrites++;
            if (!await FlushProgressAsync())
                return false;
            await Task.Delay(RequestSpacing, cancellationToken);
        }

        // Persist the final partial batch, then honour pause/cancel before completion.
        if (!await FlushProgressAsync(force: true))
            return false;

        return true;
    }

    private void SaveCache(
        IDictionary<string, TerminologyCacheEntry> cacheByKey,
        string key,
        IdentifierCheck check,
        RxNormLookupResult result,
        string rawJson,
        string? rxNormVersion,
        DateTime now)
    {
        var candidates = result.ReplacementCandidates
            .Select(x => new AuditCandidateDto(x.Rxcui, x.Name, x.TermType, x.IsActive ?? false))
            .ToList();
        if (cacheByKey.TryGetValue(key, out var entry))
        {
            entry.Status = result.Status;
            entry.ConceptName = result.ConceptName;
            entry.CanonicalRxcui = result.RelatedRxcui;
            entry.CandidatesJson = JsonSerializer.Serialize(candidates);
            entry.ReleaseStartDate = result.ReleaseStartDate;
            entry.ReleaseEndDate = result.ReleaseEndDate;
            entry.RawResponseJson = rawJson;
            entry.RxNormVersion = rxNormVersion;
            entry.FetchedUtc = now;
            entry.ExpiresUtc = now.Add(CacheLifetime);
        }
        else
        {
            entry = new TerminologyCacheEntry
            {
                IdentifierType = check.Type,
                Identifier = check.NormalizedIdentifier,
                Status = result.Status,
                ConceptName = result.ConceptName,
                CanonicalRxcui = result.RelatedRxcui,
                CandidatesJson = JsonSerializer.Serialize(candidates),
                ReleaseStartDate = result.ReleaseStartDate,
                ReleaseEndDate = result.ReleaseEndDate,
                RawResponseJson = rawJson,
                RxNormVersion = rxNormVersion,
                FetchedUtc = now,
                ExpiresUtc = now.Add(CacheLifetime)
            };
            db.TerminologyCacheEntries.Add(entry);
            cacheByKey[key] = entry;
        }
    }

    private static MedicationAuditFinding CreateFinding(
        Guid auditRunId,
        RowIdentifierCheck check,
        IReadOnlyDictionary<string, RxNormLookupResult> results)
    {
        if (check.Type == "None")
        {
            return new MedicationAuditFinding
            {
                AuditRunId = auditRunId,
                MedicationRecordId = check.Record.Id,
                SourceRowNumber = check.Record.SourceRowNumber,
                ExternalId = check.Record.ExternalId,
                MedicationName = check.Record.MedicationName,
                IdentifierType = "None",
                SubmittedIdentifier = null,
                Status = FindingStatuses.MissingIdentifier,
                Detail = "This source row has neither an RxCUI nor an NDC to validate.",
                CandidatesJson = "[]",
                CheckedUtc = DateTime.UtcNow
            };
        }

        var key = MakeKey(check.Type, check.NormalizedIdentifier);
        if (!results.TryGetValue(key, out var result))
            result = RxNormResponseParser.ServiceError(check.Type, "No result was returned for this identifier.");

        return new MedicationAuditFinding
        {
            AuditRunId = auditRunId,
            MedicationRecordId = check.Record.Id,
            SourceRowNumber = check.Record.SourceRowNumber,
            ExternalId = check.Record.ExternalId,
            MedicationName = check.Record.MedicationName,
            IdentifierType = check.Type,
            SubmittedIdentifier = check.SubmittedIdentifier,
            Status = result.Status == "ServiceError" ? FindingStatuses.LookupError : result.Status,
            ConceptName = result.ConceptName,
            CanonicalRxcui = result.RelatedRxcui,
            CandidatesJson = JsonSerializer.Serialize(result.ReplacementCandidates.Select(x =>
                new AuditCandidateDto(x.Rxcui, x.Name, x.TermType, x.IsActive ?? false))),
            ReleaseStartDate = result.ReleaseStartDate,
            ReleaseEndDate = result.ReleaseEndDate,
            Detail = result.Message,
            CheckedUtc = DateTime.UtcNow
        };
    }

    private static void AddIdentifierCheck(
        MedicationRecord record,
        string type,
        string submittedIdentifier,
        ICollection<RowIdentifierCheck> rowChecks,
        IDictionary<string, IdentifierCheck> distinctChecks)
    {
        var normalized = NormalizeIdentifier(type, submittedIdentifier);
        string? invalidMessage = null;
        if (type == "RxCUI" && !IsDigits(normalized))
            invalidMessage = "RxCUI values must contain digits only. This value was not sent to RxNorm.";
        else if (type == "NDC" && !IsValidNdc(normalized))
            invalidMessage = "NDC must be 11 digits or use a supported segmented format: 5-3-2, 5-4-1, 4-4-2 or 5-4-2. Ten unformatted digits are ambiguous and are not padded automatically.";

        // Retain distinct original invalid values so they do not collide after normalization.
        var keyIdentifier = invalidMessage is null ? normalized : submittedIdentifier.Trim().ToUpperInvariant();
        var key = MakeKey(type, keyIdentifier);
        rowChecks.Add(new RowIdentifierCheck(record, type, submittedIdentifier.Trim(), keyIdentifier));
        if (!distinctChecks.ContainsKey(key))
            distinctChecks[key] = new IdentifierCheck(type, keyIdentifier, invalidMessage, submittedIdentifier.Trim());
    }

    private static string NormalizeIdentifier(string type, string value) => value.Trim();

    private static bool IsDigits(string value) => value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static bool IsValidNdc(string value)
    {
        if (value.Length == 11 && IsDigits(value)) return true;
        var parts = value.Split('-');
        if (parts.Any(part => part.Length == 0 || !IsDigits(part))) return false;
        return parts.Length == 3 &&
            (parts[0].Length == 5 && parts[1].Length == 3 && parts[2].Length == 2
             || parts[0].Length == 5 && parts[1].Length == 4 && parts[2].Length == 1
             || parts[0].Length == 4 && parts[1].Length == 4 && parts[2].Length == 2
             || parts[0].Length == 5 && parts[1].Length == 4 && parts[2].Length == 2);
    }

    private static string MakeKey(string type, string normalized) => $"{type.ToUpperInvariant()}:{normalized}";
    private static string CacheKey(TerminologyCacheEntry entry) => MakeKey(entry.IdentifierType, entry.Identifier);

    private sealed record IdentifierCheck(string Type, string NormalizedIdentifier, string? InvalidFormatMessage, string SubmittedIdentifier);
    private sealed record RowIdentifierCheck(MedicationRecord Record, string Type, string SubmittedIdentifier, string NormalizedIdentifier)
    {
        public static RowIdentifierCheck Missing(MedicationRecord record) => new(record, "None", string.Empty, string.Empty);
    }
}

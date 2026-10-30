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
    private const int MaxUniqueIdentifiersPerAudit = 1000;

    public async Task ProcessRunAsync(Guid auditRunId, CancellationToken cancellationToken)
    {
        var run = await db.MedicationAuditRuns
            .FirstOrDefaultAsync(x => x.Id == auditRunId, cancellationToken);
        if (run is null || run.Status is MedicationAuditStatuses.Completed or MedicationAuditStatuses.Failed)
            return;

        run.Status = MedicationAuditStatuses.Running;
        run.StartedUtc = DateTime.UtcNow;
        run.ErrorMessage = null;
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

            if (distinctChecks.Count > MaxUniqueIdentifiersPerAudit)
                throw new InvalidOperationException(
                    $"This MVP supports up to {MaxUniqueIdentifiersPerAudit} distinct identifiers per audit. Split the catalogue into smaller imports.");

            run.TotalRecords = records.Count;
            var missingIdentifierRows = rowChecks.Count(x => x.Type == "None");
            run.TotalIdentifiers = distinctChecks.Count + missingIdentifierRows;
            run.ProcessedIdentifiers = missingIdentifierRows;
            await db.SaveChangesAsync(cancellationToken);

            var lookupResults = await ResolveIdentifiersAsync(run, distinctChecks, cancellationToken);
            var findings = rowChecks.Select(check => CreateFinding(run.Id, check, lookupResults)).ToList();

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

            db.MedicationAuditFindings.AddRange(findings);
            run.Status = MedicationAuditStatuses.Completed;
            run.CompletedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker will re-queue a still-running audit after the next process start.
            throw;
        }
        catch (Exception exception)
        {
            run.Status = MedicationAuditStatuses.Failed;
            run.ErrorMessage = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
            run.CompletedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogError(exception, "Medication audit {AuditRunId} failed.", auditRunId);
        }
    }

    private async Task<Dictionary<string, RxNormLookupResult>> ResolveIdentifiersAsync(
        MedicationAuditRun run,
        IReadOnlyDictionary<string, IdentifierCheck> checks,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, RxNormLookupResult>(StringComparer.Ordinal);
        if (checks.Count == 0) return results;

        var typesAndIds = checks.Values
            .Select(x => (x.Type, x.NormalizedIdentifier))
            .Distinct()
            .ToArray();
        var cacheTypes = typesAndIds.Select(x => x.Type).Distinct().ToArray();
        var cacheIds = typesAndIds.Select(x => x.NormalizedIdentifier).Distinct().ToArray();
        var cacheEntries = await db.TerminologyCacheEntries
            .Where(x => cacheTypes.Contains(x.IdentifierType) && cacheIds.Contains(x.Identifier))
            .ToListAsync(cancellationToken);
        var cacheByKey = cacheEntries.ToDictionary(CacheKey, StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        foreach (var (key, check) in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (check.InvalidFormatMessage is not null)
            {
                results[key] = RxNormResponseParser.InvalidFormat(check.Type, check.SubmittedIdentifier, check.InvalidFormatMessage);
                run.ProcessedIdentifiers++;
                await db.SaveChangesAsync(cancellationToken);
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
                    run.ProcessedIdentifiers++;
                    await db.SaveChangesAsync(cancellationToken);
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

            run.ProcessedIdentifiers++;
            await db.SaveChangesAsync(cancellationToken);
            await Task.Delay(RequestSpacing, cancellationToken);
        }

        return results;
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
            invalidMessage = "NDC must be 11 digits or use a supported segmented format: 5-3-2, 5-4-1 or 4-4-2. Ten unformatted digits are ambiguous and are not padded automatically.";

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
             || parts[0].Length == 4 && parts[1].Length == 4 && parts[2].Length == 2);
    }

    private static string MakeKey(string type, string normalized) => $"{type.ToUpperInvariant()}:{normalized}";
    private static string CacheKey(TerminologyCacheEntry entry) => MakeKey(entry.IdentifierType, entry.Identifier);

    private sealed record IdentifierCheck(string Type, string NormalizedIdentifier, string? InvalidFormatMessage, string SubmittedIdentifier);
    private sealed record RowIdentifierCheck(MedicationRecord Record, string Type, string SubmittedIdentifier, string NormalizedIdentifier)
    {
        public static RowIdentifierCheck Missing(MedicationRecord record) => new(record, "None", string.Empty, string.Empty);
    }
}

namespace MediDrift.Api.Models;

public sealed record AuditCandidateDto(string Rxcui, string Name, string? TermType, bool Active);

public sealed record MedicationAuditRunDto(
    Guid Id,
    Guid ImportId,
    string Status,
    int TotalRecords,
    int TotalIdentifiers,
    int ProcessedIdentifiers,
    int ProgressPercent,
    int ActiveCount,
    int ObsoleteCount,
    int RemappedCount,
    int QuantifiedCount,
    int NotCurrentCount,
    int UnknownCount,
    int AlienCount,
    int InvalidFormatCount,
    int MissingIdentifierCount,
    int LookupErrorCount,
    string? RxNormVersion,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    string? ErrorMessage);

public sealed record MedicationAuditFindingDto(
    long Id,
    long MedicationRecordId,
    int SourceRowNumber,
    string? ExternalId,
    string? MedicationName,
    string IdentifierType,
    string? SubmittedIdentifier,
    string Status,
    string? ConceptName,
    string? CanonicalRxcui,
    IReadOnlyList<AuditCandidateDto> Candidates,
    string? ReleaseStartDate,
    string? ReleaseEndDate,
    string? Detail,
    DateTime CheckedUtc);

public sealed record MedicationAuditStartedDto(MedicationAuditRunDto Audit, string Message);

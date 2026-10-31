namespace MediDrift.Api.Models;

public static class MedicationAuditStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Pausing = "Pausing";
    public const string Paused = "Paused";
    public const string Cancelling = "Cancelling";
    public const string Cancelled = "Cancelled";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class FindingStatuses
{
    public const string Active = "Active";
    public const string Obsolete = "Obsolete";
    public const string Remapped = "Remapped";
    public const string Quantified = "Quantified";
    public const string NotCurrent = "NotCurrent";
    public const string Unknown = "Unknown";
    public const string Alien = "Alien";
    public const string InvalidFormat = "InvalidFormat";
    public const string MissingIdentifier = "MissingIdentifier";
    public const string LookupError = "LookupError";
}

public sealed class MedicationAuditRun
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ImportJobId { get; set; }
    public string Status { get; set; } = MedicationAuditStatuses.Queued;
    public int TotalRecords { get; set; }
    public int TotalIdentifiers { get; set; }
    public int ProcessedIdentifiers { get; set; }
    public int ActiveCount { get; set; }
    public int ObsoleteCount { get; set; }
    public int RemappedCount { get; set; }
    public int QuantifiedCount { get; set; }
    public int NotCurrentCount { get; set; }
    public int UnknownCount { get; set; }
    public int AlienCount { get; set; }
    public int InvalidFormatCount { get; set; }
    public int MissingIdentifierCount { get; set; }
    public int LookupErrorCount { get; set; }
    public string? RxNormVersion { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class MedicationAuditFinding
{
    public long Id { get; set; }
    public Guid AuditRunId { get; set; }
    public long MedicationRecordId { get; set; }
    public int SourceRowNumber { get; set; }
    public string? ExternalId { get; set; }
    public string? MedicationName { get; set; }
    public string IdentifierType { get; set; } = string.Empty;
    public string? SubmittedIdentifier { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ConceptName { get; set; }
    public string? CanonicalRxcui { get; set; }
    public string CandidatesJson { get; set; } = "[]";
    public string? ReleaseStartDate { get; set; }
    public string? ReleaseEndDate { get; set; }
    public string? Detail { get; set; }
    public DateTime CheckedUtc { get; set; }
}

public sealed class TerminologyCacheEntry
{
    public long Id { get; set; }
    public string IdentifierType { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ConceptName { get; set; }
    public string? CanonicalRxcui { get; set; }
    public string CandidatesJson { get; set; } = "[]";
    public string? ReleaseStartDate { get; set; }
    public string? ReleaseEndDate { get; set; }
    public string RawResponseJson { get; set; } = "{}";
    public string? RxNormVersion { get; set; }
    public DateTime FetchedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

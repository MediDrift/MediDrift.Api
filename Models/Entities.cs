namespace MediDrift.Api.Models;

public static class DefaultWorkspace
{
    // Single-workspace development mode. Add authentication and workspace resolution before production use.
    public static readonly Guid Id = Guid.Parse("88d9c181-b4b6-4ac9-9bd2-b2e21c2f0c5e");
    public const string Name = "My Workspace";
}

public static class ImportStatuses
{
    public const string Draft = "Draft";
    public const string Mapped = "Mapped";
    public const string Imported = "Imported";
}

public static class MappingFields
{
    public const string Ignore = "ignore";
    public const string Id = "id";
    public const string Name = "name";
    public const string Rxcui = "rxcui";
    public const string Ndc = "ndc";
    public const string Strength = "strength";
    public const string DosageForm = "dosageForm";
    public const string BrandName = "brandName";

    public static readonly string[] All =
    [
        Id, Name, Rxcui, Ndc, Strength, DosageForm, BrandName
    ];

    public static bool IsSupported(string? field) =>
        field is not null && (field == Ignore || All.Contains(field, StringComparer.Ordinal));
}

public sealed class Workspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public ICollection<ImportJob> Imports { get; set; } = new List<ImportJob>();
    public ICollection<MappingProfile> MappingProfiles { get; set; } = new List<MappingProfile>();
}

public sealed class ImportJob
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public string? WorksheetName { get; set; }
    public string Status { get; set; } = ImportStatuses.Draft;
    public int TotalRows { get; set; }
    public string MappingsJson { get; set; } = "{}";
    public string? AppliedProfileName { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ImportedUtc { get; set; }
    public ICollection<ImportColumn> Columns { get; set; } = new List<ImportColumn>();
    public ICollection<StagedImportRow> StagedRows { get; set; } = new List<StagedImportRow>();
    public ICollection<MedicationRecord> MedicationRecords { get; set; } = new List<MedicationRecord>();
}

public sealed class ImportColumn
{
    public long Id { get; set; }
    public Guid ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public int Position { get; set; }
    public string? SuggestedTargetField { get; set; }
    public double SuggestionConfidence { get; set; }
    public bool NeedsReview { get; set; }
}

public sealed class StagedImportRow
{
    public long Id { get; set; }
    public Guid ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }
    public int RowNumber { get; set; }
    public string ValuesJson { get; set; } = "{}";
}

public sealed class MappingProfile
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MappingsJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class MedicationRecord
{
    public long Id { get; set; }
    public Guid ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }
    public int SourceRowNumber { get; set; }
    public string? ExternalId { get; set; }
    public string? MedicationName { get; set; }
    public string? RxCUI { get; set; }
    public string? NDC { get; set; }
    public string? Strength { get; set; }
    public string? DosageForm { get; set; }
    public string? BrandName { get; set; }
    public string OriginalValuesJson { get; set; } = "{}";
}

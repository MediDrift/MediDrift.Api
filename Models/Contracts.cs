namespace MediDrift.Api.Models;

public sealed record ColumnMappingDto(string SourceColumn, string TargetField);

public sealed record SaveMappingRequest(
    IReadOnlyList<ColumnMappingDto> Mappings,
    string? SaveProfileName);

public sealed record CreateMappingProfileRequest(
    string Name,
    IReadOnlyList<ColumnMappingDto> Mappings);

public sealed record ImportColumnDto(
    string SourceName,
    int Position,
    string? SuggestedTargetField,
    double SuggestionConfidence,
    bool NeedsReview);

public sealed record ImportDetailsDto(
    Guid Id,
    string FileName,
    string FileExtension,
    string? WorksheetName,
    string Status,
    int TotalRows,
    DateTime CreatedUtc,
    string? AppliedProfileName,
    IReadOnlyList<ImportColumnDto> Columns,
    IReadOnlyList<Dictionary<string, string?>> SampleRows,
    IReadOnlyDictionary<string, string> SavedMapping);

public sealed record ImportListItemDto(
    Guid Id,
    string FileName,
    string Status,
    int TotalRows,
    DateTime CreatedUtc,
    DateTime? ImportedUtc);

public sealed record MappingProfileDto(
    Guid Id,
    string Name,
    IReadOnlyDictionary<string, string> Mappings,
    DateTime UpdatedUtc);

public sealed record MedicationRecordDto(
    long Id,
    int SourceRowNumber,
    string? ExternalId,
    string? MedicationName,
    string? Rxcui,
    string? Ndc,
    string? Strength,
    string? DosageForm,
    string? BrandName);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record ConfirmImportResponse(
    Guid ImportId,
    string Status,
    int ImportedRecords,
    string Message);

public sealed record ApiError(string Message, IReadOnlyList<string>? Details = null);

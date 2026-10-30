using System.Text.Json;
using MediDrift.Api.Models;

namespace MediDrift.Api.Services;

public sealed record ColumnSuggestion(
    string SourceName,
    string? TargetField,
    double Confidence,
    bool NeedsReview);

public sealed class ColumnMappingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> HeaderAliases = BuildAliases();

    public IReadOnlyList<ColumnSuggestion> Suggest(
        IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, string>? savedProfile = null)
    {
        var suggestions = new List<ColumnSuggestion>();

        foreach (var header in headers)
        {
            if (savedProfile is not null && savedProfile.TryGetValue(header, out var savedTarget)
                && MappingFields.IsSupported(savedTarget))
            {
                suggestions.Add(new ColumnSuggestion(
                    header,
                    savedTarget == MappingFields.Ignore ? null : savedTarget,
                    1.0,
                    savedTarget == MappingFields.Ignore));
                continue;
            }

            var normalized = Normalize(header);
            if (HeaderAliases.TryGetValue(normalized, out var target))
                suggestions.Add(new ColumnSuggestion(header, target, 0.99, false));
            else
                suggestions.Add(new ColumnSuggestion(header, null, 0, true));
        }

        // A canonical field can only be mapped once. Ambiguous duplicate suggestions must be reviewed.
        var duplicates = suggestions
            .Where(x => x.TargetField is not null)
            .GroupBy(x => x.TargetField!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(x => x.SourceName)
            .ToHashSet(StringComparer.Ordinal);

        return suggestions.Select(x => duplicates.Contains(x.SourceName)
            ? x with { TargetField = null, Confidence = 0, NeedsReview = true }
            : x).ToArray();
    }

    public IReadOnlyList<string> Validate(
        IReadOnlyList<string> availableHeaders,
        IReadOnlyList<ColumnMappingDto>? mappings)
    {
        var errors = new List<string>();
        if (mappings is null || mappings.Count == 0)
        {
            errors.Add("At least one column mapping is required.");
            return errors;
        }

        var available = availableHeaders.ToHashSet(StringComparer.Ordinal);
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.SourceColumn))
            {
                errors.Add("A source column name is missing.");
                continue;
            }
            if (!available.Contains(mapping.SourceColumn))
                errors.Add($"Source column '{mapping.SourceColumn}' does not exist in the uploaded file.");
            if (!seenSources.Add(mapping.SourceColumn))
                errors.Add($"Source column '{mapping.SourceColumn}' is mapped more than once.");
            if (!MappingFields.IsSupported(mapping.TargetField))
                errors.Add($"Target field '{mapping.TargetField}' is not supported.");
            else if (mapping.TargetField != MappingFields.Ignore && !seenTargets.Add(mapping.TargetField))
                errors.Add($"More than one source column is mapped to '{mapping.TargetField}'. Each target field can only be mapped once.");
        }

        return errors;
    }

    public static Dictionary<string, string> ToDictionary(IEnumerable<ColumnMappingDto> mappings) =>
        mappings.ToDictionary(x => x.SourceColumn, x => x.TargetField, StringComparer.Ordinal);

    public static Dictionary<string, string> DeserializeMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public static string SerializeMappings(IReadOnlyDictionary<string, string> mappings) =>
        JsonSerializer.Serialize(mappings, JsonOptions);

    public static Dictionary<string, string?> DeserializeRow(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(json, JsonOptions)
        ?? new Dictionary<string, string?>(StringComparer.Ordinal);

    public static string SerializeRow(IReadOnlyDictionary<string, string?> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static Dictionary<string, string> BuildAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(MappingFields.Id, "id", "drugid", "medicationid", "externalid", "drugidentifier", "productid", "itemid");
        Add(MappingFields.Name, "name", "medicationname", "drugname", "drugdescription", "description", "productname", "medicationdescription");
        Add(MappingFields.Rxcui, "rxcui", "rxcuicode", "rxcuiid", "rxnormid", "rxnormcode", "rxnormconceptid");
        Add(MappingFields.Ndc, "ndc", "ndccode", "nationaldrugcode", "nationaldrugcodevalue");
        Add(MappingFields.Strength, "strength", "strengthvalue", "drugstrength", "medicationstrength", "dose");
        Add(MappingFields.DosageForm, "dosageform", "doseform", "form", "drugform", "medicationform");
        Add(MappingFields.BrandName, "brand", "brandname", "brandedname", "tradename");
        return aliases;

        void Add(string target, params string[] values)
        {
            foreach (var value in values) aliases[Normalize(value)] = target;
        }
    }
}

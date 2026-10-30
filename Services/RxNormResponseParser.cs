using System.Text.Json;

namespace MediDrift.Api.Services;

public sealed record RxNormCandidate(string Rxcui, string Name, string? TermType, bool? IsActive);

public sealed record RxNormLookupResult(
    string Status,
    string Severity,
    string Message,
    string? ConceptName,
    string? RelatedRxcui,
    string? TermType,
    string? ReleaseStartDate,
    string? ReleaseEndDate,
    IReadOnlyList<RxNormCandidate> ReplacementCandidates,
    string SourceApi,
    string? RawJson,
    bool IsCached = false);

public static class RxNormResponseParser
{
    public static RxNormLookupResult ParseRxcui(string rawJson, bool isCached = false)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        // RxNav's live JSON responses currently expose rxcuiStatusHistory at the root;
        // tolerate an rxnormdata wrapper as well for compatibility with other representations.
        var wrappedData = GetObject(root, "rxnormdata");
        var data = wrappedData.ValueKind == JsonValueKind.Object ? wrappedData : root;
        var history = GetObject(data, "rxcuiStatusHistory");
        var metadata = GetObject(history, "metaData");
        var attributes = GetObject(history, "attributes");
        var derived = GetObject(history, "derivedConcepts");

        var status = GetString(metadata, "status") ?? "Unknown";
        var name = GetString(attributes, "name");
        var relatedRxcui = GetString(attributes, "rxcui");
        var termType = GetString(attributes, "tty");
        var releaseStart = GetString(metadata, "releaseStartDate");
        var replacements = new List<RxNormCandidate>();

        foreach (var candidate in GetElements(derived, "remappedConcept"))
        {
            var id = GetString(candidate, "remappedRxCui");
            if (!string.IsNullOrWhiteSpace(id))
            {
                replacements.Add(new RxNormCandidate(
                    id,
                    GetString(candidate, "remappedName") ?? "Unnamed replacement concept",
                    GetString(candidate, "remappedTTY"),
                    ParseYesNo(GetString(candidate, "remappedActive"))));
            }
        }

        foreach (var candidate in GetElements(derived, "quantifiedConcept"))
        {
            var id = GetString(candidate, "quantifiedRxcui");
            if (!string.IsNullOrWhiteSpace(id) && replacements.All(x => x.Rxcui != id))
            {
                replacements.Add(new RxNormCandidate(
                    id,
                    GetString(candidate, "quantifiedName") ?? "Quantified concept",
                    GetString(candidate, "quantifiedTTY"),
                    ParseYesNo(GetString(candidate, "quantifiedActive"))));
            }
        }

        var normalizedStatus = NormalizeStatus(status);
        if (normalizedStatus == "Remapped")
            relatedRxcui = replacements.FirstOrDefault(x => x.IsActive == true)?.Rxcui ?? relatedRxcui;

        var (severity, message) = normalizedStatus switch
        {
            "Active" => ("Info", "RxNorm recognizes this RxCUI as an active concept."),
            "Obsolete" => ("High", "RxNorm marks this concept obsolete. Review the medication record before using it."),
            "Remapped" => ("Review", replacements.Count > 0
                ? "RxNorm reports that this concept has been remapped. Review the candidate concepts; MediDrift has not changed the identifier."
                : "RxNorm reports that this concept has been remapped, but no candidate was included in the response."),
            "Quantified" => ("Review", "RxNorm identifies this concept as a non-dispensable quantified concept. Review the related quantified concepts."),
            "NotCurrent" => ("High", "This concept is not current in RxNorm. Review its history and any available candidates."),
            _ => ("Error", "RxNorm has never reported this RxCUI in a monthly release, or it could not be recognized.")
        };

        return new RxNormLookupResult(normalizedStatus, severity, message, name, relatedRxcui, termType,
            EmptyToNull(releaseStart), EmptyToNull(GetString(metadata, "releaseEndDate")), replacements, "RxNorm RxCUI history status", rawJson, isCached);
    }

    public static RxNormLookupResult ParseNdc(string rawJson, bool isCached = false)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var wrappedData = GetObject(root, "rxnormdata");
        var data = wrappedData.ValueKind == JsonValueKind.Object ? wrappedData : root;
        var ndcStatus = GetObject(data, "ndcStatus");
        var rawStatus = GetString(ndcStatus, "status") ?? "UNKNOWN";
        var status = rawStatus.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" => "Active",
            "OBSOLETE" => "Obsolete",
            "ALIEN" => "Alien",
            _ => "Unknown"
        };

        var replacements = new List<RxNormCandidate>();
        foreach (var history in GetElements(ndcStatus, "ndcHistory"))
        {
            var activeRxcui = GetString(history, "activeRxcui");
            if (!string.IsNullOrWhiteSpace(activeRxcui) && replacements.All(x => x.Rxcui != activeRxcui))
            {
                replacements.Add(new RxNormCandidate(activeRxcui, "RxNorm concept associated with NDC history", null, true));
            }
        }

        var relatedRxcui = GetString(ndcStatus, "rxcui");
        var relatedName = GetString(ndcStatus, "conceptName");

        var (severity, message) = status switch
        {
            "Active" => ("Info", "RxNorm recognizes this NDC as active and associates it with an active concept."),
            "Obsolete" => ("High", "RxNorm reports that this NDC is obsolete. Review the package identifier and its history."),
            "Alien" => ("Review", "This NDC appears in a non-RxNorm source vocabulary. It is not an active RxNorm NDC; review the source and associated concept."),
            _ => ("Error", "RxNorm does not recognize this NDC as active or historical. Verify the digits and packaging identifier.")
        };

        if (string.IsNullOrWhiteSpace(relatedRxcui) && replacements.Count > 0)
            relatedRxcui = replacements[0].Rxcui;

        return new RxNormLookupResult(status, severity, message, relatedName, relatedRxcui,
            null, null, null, replacements, "RxNorm NDC status", rawJson, isCached);
    }

    public static RxNormLookupResult InvalidFormat(string identifierType, string originalValue, string explanation) =>
        new("InvalidFormat", "Error", explanation, null, null, null, null, null, [],
            "Local format check", null);

    public static RxNormLookupResult ServiceError(string identifierType, string error) =>
        new("ServiceError", "Error", $"The RxNorm service could not be reached for this {identifierType}. {error}",
            null, null, null, null, null, [], "RxNorm API", null);

    private static JsonElement GetObject(JsonElement parent, string property)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Object)
            return value;
        return default;
    }

    private static string? GetString(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => EmptyToNull(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static IReadOnlyList<JsonElement> GetElements(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value)) return [];
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
        if (value.ValueKind == JsonValueKind.Object) return [value];
        return [];
    }

    private static bool? ParseYesNo(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "YES" => true,
        "NO" => false,
        _ => null
    };

    private static string NormalizeStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "active" => "Active",
        "obsolete" => "Obsolete",
        "remapped" => "Remapped",
        "quantified" => "Quantified",
        "notcurrent" => "NotCurrent",
        _ => "Unknown"
    };

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

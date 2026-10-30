using System.Text.Json;

namespace MediDrift.Api.Services;

public sealed class RxNormClient(HttpClient httpClient)
{
    public async Task<string> GetRxcuiHistoryStatusAsync(string rxcui, CancellationToken cancellationToken)
    {
        var path = $"REST/rxcui/{Uri.EscapeDataString(rxcui)}/historystatus.json";
        return await GetJsonAsync(path, cancellationToken);
    }

    public async Task<string> GetNdcStatusAsync(string ndc, CancellationToken cancellationToken)
    {
        var path = $"REST/ndcstatus.json?ndc={Uri.EscapeDataString(ndc)}&history=1&altpkg=0";
        return await GetJsonAsync(path, cancellationToken);
    }

    public async Task<string?> GetRxNormVersionAsync(CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync("REST/version.json", cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var data = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rxnormdata", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object)
            data = wrapped;
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("version", out var version)
            ? version.GetString()
            : null;
    }

    private async Task<string> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return body;
    }
}

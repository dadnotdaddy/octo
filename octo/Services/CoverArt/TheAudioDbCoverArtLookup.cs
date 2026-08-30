using System.Text.Json;
using Octo.Services.Soulseek;

namespace Octo.Services.CoverArt;

/// <summary>
/// Artist images via TheAudioDB, using their published free test key ("123" --
/// documented on theaudiodb.com, not a real customer credential, so it's fine
/// to keep in source rather than treating it as a secret env var). Second-tier
/// fallback behind Fanart.tv: name-matched (no MBID lookup, so slightly less
/// precise) but covers artists Fanart.tv's community-driven catalog misses.
///
/// Artist-only: returns null immediately for Song/Album routings.
///
/// Endpoint: https://www.theaudiodb.com/api/v1/json/123/search.php?s=Name
/// Returns "artists" array with strArtistThumb / strArtistFanart / strArtistWideThumb.
/// </summary>
public class TheAudioDbCoverArtLookup : ICoverArtSource
{
    private const string TestApiKey = "123";
    private readonly HttpClient _http;
    private readonly ILogger<TheAudioDbCoverArtLookup> _logger;

    public string Name => "theaudiodb";

    public TheAudioDbCoverArtLookup(IHttpClientFactory httpClientFactory, ILogger<TheAudioDbCoverArtLookup> logger)
    {
        _http = httpClientFactory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(8);
        _logger = logger;
    }

    public async Task<byte[]?> TryFetchAsync(SoulseekRouting routing, CancellationToken ct = default)
    {
        if (routing.Kind != RoutingKind.Artist) return null;

        var artist = (routing.Artist ?? "").Trim();
        if (string.IsNullOrEmpty(artist)) return null;

        try
        {
            var imageUrl = await ResolveImageAsync(artist, ct);
            if (string.IsNullOrEmpty(imageUrl)) return null;

            using var resp = await _http.GetAsync(imageUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "theaudiodb lookup failed for artist {Artist}", artist);
            return null;
        }
    }

    private async Task<string?> ResolveImageAsync(string artist, CancellationToken ct)
    {
        var url = $"https://www.theaudiodb.com/api/v1/json/{TestApiKey}/search.php?s={Uri.EscapeDataString(artist)}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array || artists.GetArrayLength() == 0)
            return null;

        string? best = null;
        int bestScore = int.MinValue;
        foreach (var item in artists.EnumerateArray())
        {
            var name = item.TryGetProperty("strArtist", out var n) ? n.GetString() ?? "" : "";
            var img = ReadString(item, "strArtistThumb") ?? ReadString(item, "strArtistFanart") ?? ReadString(item, "strArtistWideThumb");
            if (string.IsNullOrEmpty(img)) continue;
            var score = ScoreNameMatch(artist, name);
            if (score > bestScore)
            {
                bestScore = score;
                best = img;
            }
        }
        return bestScore >= 60 ? best : null;
    }

    private static string? ReadString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static int ScoreNameMatch(string expected, string actual)
    {
        if (string.IsNullOrEmpty(actual)) return 0;
        var e = expected.Trim().ToLowerInvariant();
        var a = actual.Trim().ToLowerInvariant();
        if (a == e) return 100;
        if (a.Contains(e) || e.Contains(a)) return 60;
        var eTokens = e.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return eTokens.Count(t => aTokens.Contains(t)) * 10;
    }
}

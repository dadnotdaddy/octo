using System.Text.Json;
using Octo.Services.Soulseek;

namespace Octo.Services.CoverArt;

/// <summary>
/// Artist images via Fanart.tv, keyed by MusicBrainz artist ID rather than
/// fuzzy name matching (unlike Deezer's artist search, which mismatches on
/// common/ambiguous artist names). Resolves the MBID via MusicBrainz's free
/// search API first, then fetches Fanart.tv's artist image set.
///
/// Artist-only: returns null immediately for Song/Album routings so the
/// aggregator falls through to Deezer/iTunes for those.
///
/// Endpoints used:
///   GET https://musicbrainz.org/ws/2/artist/?query=artist:"Name"&fmt=json
///   GET https://webservice.fanart.tv/v3/music/{mbid}?api_key=KEY
///
/// Fanart.tv returns "artistthumb" (square-ish promo photo) and
/// "artistbackground" (wide backdrop) arrays, each already sorted by
/// community likes; we take the first thumb, falling back to the first
/// background if no thumb exists.
/// </summary>
public class FanartCoverArtLookup : ICoverArtSource
{
    private readonly HttpClient _http;
    private readonly ILogger<FanartCoverArtLookup> _logger;
    private readonly string? _apiKey;

    public string Name => "fanart.tv";

    public FanartCoverArtLookup(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<FanartCoverArtLookup> logger)
    {
        _http = httpClientFactory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("octorr/1.0 (+https://github.com/)");
        _logger = logger;
        _apiKey = config["FANART_API_KEY"] ?? Environment.GetEnvironmentVariable("FANART_API_KEY");
    }

    public async Task<byte[]?> TryFetchAsync(SoulseekRouting routing, CancellationToken ct = default)
    {
        if (routing.Kind != RoutingKind.Artist) return null;
        if (string.IsNullOrEmpty(_apiKey)) return null;

        var artist = (routing.Artist ?? "").Trim();
        if (string.IsNullOrEmpty(artist)) return null;

        try
        {
            var mbid = await ResolveMbidAsync(artist, ct);
            if (string.IsNullOrEmpty(mbid)) return null;

            var imageUrl = await ResolveFanartImageAsync(mbid, ct);
            if (string.IsNullOrEmpty(imageUrl)) return null;

            using var resp = await _http.GetAsync(imageUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "fanart.tv lookup failed for artist {Artist}", artist);
            return null;
        }
    }

    private async Task<string?> ResolveMbidAsync(string artist, CancellationToken ct)
    {
        var q = $"artist:\"{artist}\"";
        var url = $"https://musicbrainz.org/ws/2/artist/?query={Uri.EscapeDataString(q)}&fmt=json&limit=5";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("artists", out var artists) || artists.GetArrayLength() == 0)
            return null;

        string? best = null;
        int bestScore = int.MinValue;
        foreach (var item in artists.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;
            var score = ScoreNameMatch(artist, name);
            if (score > bestScore)
            {
                bestScore = score;
                best = id;
            }
        }
        // Require a reasonably confident match -- MusicBrainz will happily return
        // something for near-garbage queries, and a wrong MBID means a wrong
        // artist's photo, which is worse than no photo.
        return bestScore >= 60 ? best : null;
    }

    private async Task<string?> ResolveFanartImageAsync(string mbid, CancellationToken ct)
    {
        var url = $"https://webservice.fanart.tv/v3/music/{mbid}?api_key={Uri.EscapeDataString(_apiKey!)}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("artistthumb", out var thumbs) && thumbs.GetArrayLength() > 0)
        {
            var url0 = thumbs[0].TryGetProperty("url", out var u) ? u.GetString() : null;
            if (!string.IsNullOrEmpty(url0)) return url0;
        }
        if (doc.RootElement.TryGetProperty("artistbackground", out var backgrounds) && backgrounds.GetArrayLength() > 0)
        {
            var url0 = backgrounds[0].TryGetProperty("url", out var u) ? u.GetString() : null;
            if (!string.IsNullOrEmpty(url0)) return url0;
        }
        return null;
    }

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

using System.Text;
using System.Text.Json;

namespace Octo.Services.LastFm;

/// <summary>
/// Similar-tracks via ListenBrainz's free public "labs" API -- no account,
/// no token, no scrobbling/connection to Navidrome required. Two calls:
///
///   1. acr-lookup: resolve a recording MBID from artist+title text.
///   2. similar-recordings: collaborative-filtering similarity (built from
///      real ListenBrainz user listening sessions) seeded by that MBID.
///
/// This replaces Last.fm as the *radio* seed source (Last.fm's track.getsimilar
/// leans on decade-old tag/scrobble data and is noticeably weaker/more generic
/// than ListenBrainz's session-based model). LastFmService is kept around for
/// its other jobs (search3 text search, artist top-tracks padding) since
/// ListenBrainz's labs API has no free-text search endpoint.
/// </summary>
public class ListenBrainzSimilarTracksService
{
    private const string AcrLookupUrl = "https://labs.api.listenbrainz.org/acr-lookup/json";
    private const string SimilarRecordingsUrl = "https://labs.api.listenbrainz.org/similar-recordings/json";

    // ListenBrainz's own default algorithm for their web similarity viewer;
    // picked for parity with what listenbrainz.org itself shows.
    private const string Algorithm =
        "session_based_days_7500_session_300_contribution_5_threshold_15_limit_50_skip_30";

    private readonly HttpClient _http;
    private readonly ILogger<ListenBrainzSimilarTracksService> _logger;

    public ListenBrainzSimilarTracksService(IHttpClientFactory httpClientFactory, ILogger<ListenBrainzSimilarTracksService> logger)
    {
        _http = httpClientFactory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(10);
        _logger = logger;
    }

    public async Task<List<LastFmService.SimilarTrack>> GetSimilarTracksAsync(string artist, string title, int limit = 50)
    {
        try
        {
            var mbid = await ResolveRecordingMbidAsync(artist, title);
            if (string.IsNullOrEmpty(mbid))
            {
                _logger.LogDebug("ListenBrainz acr-lookup found no recording MBID for {Artist} - {Title}", artist, title);
                return new List<LastFmService.SimilarTrack>();
            }

            return await GetSimilarByMbidAsync(mbid, limit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ListenBrainz similar-tracks lookup failed for {Artist} - {Title}", artist, title);
            return new List<LastFmService.SimilarTrack>();
        }
    }

    private async Task<string?> ResolveRecordingMbidAsync(string artist, string title)
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { artist_credit_name = artist, recording_name = title }
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(AcrLookupUrl, content);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var first = doc.RootElement[0];
        return first.TryGetProperty("recording_mbid", out var mbidEl) && mbidEl.ValueKind == JsonValueKind.String
            ? mbidEl.GetString()
            : null;
    }

    private async Task<List<LastFmService.SimilarTrack>> GetSimilarByMbidAsync(string recordingMbid, int limit)
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { recording_mbids = new[] { recordingMbid }, algorithm = Algorithm }
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(SimilarRecordingsUrl, content);
        if (!resp.IsSuccessStatusCode) return new List<LastFmService.SimilarTrack>();

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<LastFmService.SimilarTrack>();

        // Scores are unbounded ints (session-cooccurrence counts), not 0-1 --
        // normalize against this batch's own max so downstream consumers that
        // expect a 0-1 "match" (like the old Last.fm field) still get something
        // sane rather than raw counts.
        var maxScore = 1.0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
                maxScore = Math.Max(maxScore, s.GetDouble());
        }

        var tracks = new List<LastFmService.SimilarTrack>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.TryGetProperty("recording_name", out var n) ? n.GetString() : null;
            var artist = el.TryGetProperty("artist_credit_name", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(artist)) continue;

            var score = el.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0.0;
            tracks.Add(new LastFmService.SimilarTrack(artist, name, score / maxScore));
            if (tracks.Count >= limit) break;
        }

        _logger.LogInformation("ListenBrainz similar-recordings for {Mbid} -> {Count} tracks", recordingMbid, tracks.Count);
        return tracks;
    }
}

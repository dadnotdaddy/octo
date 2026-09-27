using System.Text.Json;

namespace Octo.Services.YouTube;

/// <summary>
/// Pure HTTP client for the yt-dlp-shim sidecar service. Octo never spawns
/// yt-dlp itself — the shim wraps it behind /search and /stream endpoints
/// in its own container, so any process-management quirks stay isolated.
/// </summary>
public class YouTubeResolver
{
    // Named clients registered in Program.cs. The "stream" client uses an infinite
    // timeout because /stream from the shim is open for the duration of playback;
    // the default HttpClient timeout would kill the read mid-song.
    public const string SearchClientName = "yt-dlp-shim-search";
    public const string StreamClientName = "yt-dlp-shim-stream";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeResolver> _logger;
    private readonly string _baseUrl;

    public YouTubeResolver(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<YouTubeResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _baseUrl = (configuration.GetValue<string>("YouTube:ShimUrl") ?? "http://yt-dlp-shim:8080").TrimEnd('/');
    }

    /// <summary>
    /// Resolves "Artist - Title" to a single best YouTube hit via the shim.
    /// Pass <paramref name="interactive"/> when someone is actively waiting on
    /// this (a tap on a song): the shim keeps slots reserved for that work so it
    /// never queues behind background search enrichment, which routinely backs
    /// up 10-20s when the client refreshes a whole library at once.
    /// </summary>
    public async Task<YouTubeHit?> SearchAsync(string query, int? durationHint = null, CancellationToken ct = default, bool interactive = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var url = $"{_baseUrl}/search?q={Uri.EscapeDataString(query)}"
            + (durationHint is int dh && dh > 0 ? $"&duration={dh}" : "")
            + (interactive ? "&prio=1" : "");
        try
        {
            var http = _httpClientFactory.CreateClient(SearchClientName);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("shim /search HTTP {Code} for '{Q}'", (int)resp.StatusCode, query);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var videoId = root.TryGetProperty("video_id", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(videoId)) return null;
            return new YouTubeHit
            {
                VideoId = videoId,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                Duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null,
                Channel = root.TryGetProperty("channel", out var c) ? c.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("shim /search failed for '{Q}': {Msg}", query, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Pre-resolves a video's signed URL on the shim so a later /stream is a
    /// cache hit instead of a ~3s yt-dlp launch. Fire-and-forget: a failed warm
    /// costs nothing, the play path just resolves normally.
    /// </summary>
    public async Task WarmAsync(string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return;
        try
        {
            var http = _httpClientFactory.CreateClient(SearchClientName);
            using var resp = await http.GetAsync($"{_baseUrl}/warm?id={Uri.EscapeDataString(videoId)}", ct);
            _logger.LogDebug("shim /warm {Vid} -> {Code}", videoId, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("shim /warm failed for {Vid}: {Msg}", videoId, ex.Message);
        }
    }

    /// <summary>
    /// Fast metadata-only lookup via the shim's /meta (flat search, no URL
    /// resolution). Returns the top video's id + duration for showing an accurate
    /// length without paying the full /search extraction.
    /// </summary>
    public async Task<YouTubeHit?> MetaAsync(string query, int? durationHint = null, CancellationToken ct = default, bool interactive = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var url = $"{_baseUrl}/meta?q={Uri.EscapeDataString(query)}"
            + (durationHint is int dh && dh > 0 ? $"&duration={dh}" : "")
            + (interactive ? "&prio=1" : "");
        try
        {
            var http = _httpClientFactory.CreateClient(SearchClientName);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var vid = root.TryGetProperty("video_id", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(vid)) return null;
            return new YouTubeHit
            {
                VideoId = vid,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                Duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a streaming connection to the shim's /stream endpoint. The shim
    /// proxies bytes from YouTube's CDN to us; we hand the resulting stream
    /// off to ASP.NET which forwards it to the Subsonic client.
    ///
    /// <paramref name="rangeHeader"/> is forwarded verbatim to the shim, which
    /// passes it on to googlevideo. iOS Subsonic clients (Arpeggi, Narjo) probe
    /// with `Range: bytes=0-1` and won't play audio/mp4 unless the server
    /// returns 206 with a valid Content-Range, so this passthrough is required
    /// for them to even attempt playback.
    ///
    /// Returns (stream, contentType, contentLength, statusCode, contentRange,
    /// owner) or null on failure. Caller owns the stream + response. The
    /// HttpClient itself is owned by IHttpClientFactory and must NOT be
    /// disposed — doing so tears down the handler/connection backing the
    /// returned stream and reads die mid-song.
    /// </summary>
    public async Task<(Stream stream, string contentType, long? contentLength, int statusCode, string? contentRange, HttpResponseMessage owner)?> OpenStreamAsync(string videoId, string? rangeHeader = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return null;
        var url = $"{_baseUrl}/stream?id={Uri.EscapeDataString(videoId)}";

        var http = _httpClientFactory.CreateClient(StreamClientName);

        HttpResponseMessage? resp = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                req.Headers.TryAddWithoutValidation("Range", rangeHeader);
            }
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            // Accept 200 (full body) and 206 (partial content). Anything else
            // means the upstream/shim couldn't satisfy the request.
            if ((int)resp.StatusCode != 200 && (int)resp.StatusCode != 206)
            {
                _logger.LogWarning("shim /stream HTTP {Code} for {Vid}", (int)resp.StatusCode, videoId);
                resp.Dispose();
                return null;
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "audio/mp4";
            var contentLength = resp.Content.Headers.ContentLength;
            var contentRange = resp.Content.Headers.ContentRange?.ToString();
            var statusCode = (int)resp.StatusCode;
            var taken = resp; resp = null; // ownership transferred to caller via OwningStream wrapper
            return (stream, contentType, contentLength, statusCode, contentRange, taken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("shim /stream failed for {Vid}: {Msg}", videoId, ex.Message);
            resp?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Downloads a YouTube video as MP3 to {destWithoutExt}.mp3 via the shim's
    /// /download endpoint, passing clean artist/title so the file is tagged for
    /// the library. Returns the saved path, or null on failure. Uses the
    /// infinite-timeout stream client because a download can take a while.
    /// </summary>
    public async Task<string?> DownloadAsync(string videoId, string destWithoutExt,
        string? artist = null, string? title = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(destWithoutExt)) return null;
        var url = $"{_baseUrl}/download?id={Uri.EscapeDataString(videoId)}&dest={Uri.EscapeDataString(destWithoutExt)}"
            + (string.IsNullOrEmpty(artist) ? "" : $"&artist={Uri.EscapeDataString(artist)}")
            + (string.IsNullOrEmpty(title) ? "" : $"&title={Uri.EscapeDataString(title)}");
        try
        {
            var http = _httpClientFactory.CreateClient(StreamClientName);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("shim /download HTTP {Code} for vid={Vid}", (int)resp.StatusCode, videoId);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("shim /download failed for {Vid}: {Msg}", videoId, ex.Message);
            return null;
        }
    }
}

public class YouTubeHit
{
    public string VideoId { get; set; } = "";
    public string? Title { get; set; }
    public int? Duration { get; set; }

    /// <summary>
    /// True when a YouTube video's length is plausibly the same recording as a track of
    /// <paramref name="expectedSeconds"/>: within 25% or 30s. Different uploads of one song
    /// differ by tens of seconds ("Fade" 3:13 on Deezer vs 3:45 on YouTube), but a
    /// full-album upload or a video that runs into the next track is far outside that.
    /// </summary>
    public static bool Fits(int? videoSeconds, int? expectedSeconds)
        => videoSeconds is int v && v > 0 && expectedSeconds is int e && e > 0
           && Math.Abs(v - e) <= Math.Max(30, e / 4);
    public string? Channel { get; set; }
}

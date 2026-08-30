using System.Collections.Concurrent;
using System.Text.Json;

namespace Octo.Services.Metadata;

/// <summary>
/// Enriches external (YouTube-resolved) tracks with real album/artist metadata
/// from Deezer's public API. Keyless and no ARL — the ARL that expires on the
/// music bot is only for Deezer AUDIO; metadata endpoints are open.
///
/// Everything here is best-effort and uncached: every call hits Deezer fresh so a
/// transient miss (throttle, outage) never sticks around as a false negative --
/// a Deezer outage or miss returns null and callers fall back to a synthetic
/// entity. Nothing on this path ever blocks or fails playback.
/// </summary>
public class DeezerMetadataService
{
    public record TrackMeta(string? AlbumTitle, string? AlbumCoverUrl, int? Year, int? Duration,
        string? ArtistName, string? ArtistImageUrl);
    public record ArtistMeta(string? Name, string? ImageUrl);
    /// <summary>One artist from a catalog search, for populating search3's
    /// artist results (previously always empty).</summary>
    public record ArtistHit(string DeezerId, string Name, string? ImageUrl, int? AlbumCount);

    /// <summary>Everything Deezer knows about a track, for writing rich file tags.</summary>
    public record FullTrackMeta(
        string? AlbumTitle, string? AlbumCoverUrl, int? Year, int? Duration, string? ArtistName,
        int? TrackNumber, int? DiscNumber, string? Isrc, int? TotalTracks, string? Genre,
        string? Label, string? ReleaseDate);

    /// <summary>One album from a catalog search. Year is not on the search payload;
    /// the detail call fills it.</summary>
    public record AlbumHit(string DeezerId, string Title, string Artist,
        string? CoverUrl, int? Year, int TrackCount, string? RecordType);

    /// <summary>One track of an album, with the real length and position.</summary>
    public record AlbumTrack(string Title, string Artist, int? Duration,
        int? TrackPosition, int? DiscNumber, string? Isrc);

    /// <summary>An album plus its full tracklist.</summary>
    public record AlbumDetail(string DeezerId, string Title, string Artist,
        string? CoverUrl, int? Year, string? Genre, string? Label, List<AlbumTrack> Tracks);

    private const string Base = "https://api.deezer.com";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeezerMetadataService> _logger;
    // Single-flight the album-year fetch: many tracks in one search share an album
    // (a whole album's tracks), so concurrent lookups collapse onto one HTTP call.
    // Not a result cache -- each entry is removed the moment its fetch completes,
    // so a later search still hits Deezer fresh.
    private readonly ConcurrentDictionary<long, Lazy<Task<int?>>> _albumYearTasks = new();

    public DeezerMetadataService(IHttpClientFactory httpFactory, ILogger<DeezerMetadataService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private HttpClient Client()
    {
        var c = _httpFactory.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(8);
        return c;
    }

    /// <summary>Resolve "artist + title" to the real album + artist (name, art, year).
    /// Pass includeYear=false to skip the extra album-detail call (bulk enrichment
    /// wants duration + album fast; the year is fetched lazily by the album view).</summary>
    public async Task<TrackMeta?> EnrichTrackAsync(string? artist, string? title, bool includeYear = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) return null;
        TrackMeta? meta = null;
        try
        {
            var q = Uri.EscapeDataString($"artist:\"{artist}\" track:\"{title}\"");
            using var doc = await GetJsonAsync($"{Base}/search?q={q}&limit=1", ct);
            if (FirstData(doc) is JsonElement t)
            {
                string? albTitle = null, cover = null, artName = null, artImg = null;
                long albId = 0;
                if (t.TryGetProperty("album", out var alb))
                {
                    albTitle = Str(alb, "title");
                    cover = Str(alb, "cover_xl") ?? Str(alb, "cover_medium");
                    if (alb.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number)
                        albId = aid.GetInt64();
                }
                if (t.TryGetProperty("artist", out var art))
                {
                    artName = Str(art, "name");
                    artImg = Str(art, "picture_xl") ?? Str(art, "picture_medium");
                }
                int? duration = t.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number
                    ? du.GetInt32() : null;
                var year = includeYear && albId > 0 ? await AlbumYearAsync(albId, ct) : null;
                meta = new TrackMeta(albTitle, cover, year, duration, artName, artImg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer enrich track '{A} - {T}' failed: {M}", artist, title, ex.Message);
        }

        return meta;
    }

    /// <summary>
    /// Full track metadata for tagging a downloaded file: one track search (album,
    /// cover_xl, artist, duration, track_position, disk_number, isrc) plus one album
    /// detail call (release year, genre, total tracks, label). Cached; best-effort.
    /// </summary>
    public async Task<FullTrackMeta?> EnrichTrackFullAsync(string? artist, string? title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) return null;
        FullTrackMeta? meta = null;
        try
        {
            var q = Uri.EscapeDataString($"artist:\"{artist}\" track:\"{title}\"");
            using var doc = await GetJsonAsync($"{Base}/search?q={q}&limit=1", ct);
            if (FirstData(doc) is JsonElement t)
            {
                string? albTitle = null, cover = null, artName = null;
                var isrc = Str(t, "isrc");
                long albId = 0;
                if (t.TryGetProperty("album", out var alb))
                {
                    albTitle = Str(alb, "title");
                    cover = Str(alb, "cover_xl") ?? Str(alb, "cover_big") ?? Str(alb, "cover_medium");
                    if (alb.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number)
                        albId = aid.GetInt64();
                }
                if (t.TryGetProperty("artist", out var art)) artName = Str(art, "name");

                int? year = null, totalTracks = null;
                string? genre = null, label = null, releaseDate = null;
                if (albId > 0)
                {
                    using var adoc = await GetJsonAsync($"{Base}/album/{albId}", ct);
                    if (adoc != null)
                    {
                        var root = adoc.RootElement;
                        releaseDate = Str(root, "release_date");
                        if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && int.TryParse(releaseDate[..4], out var yr))
                            year = yr;
                        totalTracks = Int(root, "nb_tracks");
                        label = Str(root, "label");
                        if (root.TryGetProperty("genres", out var g) && g.TryGetProperty("data", out var gd)
                            && gd.ValueKind == JsonValueKind.Array && gd.GetArrayLength() > 0)
                            genre = Str(gd[0], "name");
                    }
                }

                meta = new FullTrackMeta(albTitle, cover, year, Int(t, "duration"), artName,
                    Int(t, "track_position"), Int(t, "disk_number"), isrc, totalTracks, genre, label, releaseDate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer full enrich '{A} - {T}' failed: {M}", artist, title, ex.Message);
        }

        return meta;
    }

    /// <summary>Resolve an artist name to its Deezer name + image.</summary>
    public async Task<ArtistMeta?> EnrichArtistAsync(string? artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;
        ArtistMeta? meta = null;
        try
        {
            var q = Uri.EscapeDataString(artist);
            using var doc = await GetJsonAsync($"{Base}/search/artist?q={q}&limit=1", ct);
            if (FirstData(doc) is JsonElement a)
                meta = new ArtistMeta(Str(a, "name"), Str(a, "picture_xl") ?? Str(a, "picture_medium"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer enrich artist '{A}' failed: {M}", artist, ex.Message);
        }

        return meta;
    }

    /// <summary>Search the artist catalog directly (multiple results), for search3's
    /// artist tab. Distinct from <see cref="EnrichArtistAsync"/>, which takes a single
    /// known-good name and returns at most one match for tagging/enrichment.</summary>
    public async Task<List<ArtistHit>> SearchArtistsAsync(string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new List<ArtistHit>();
        var hits = new List<ArtistHit>();
        try
        {
            var q = Uri.EscapeDataString(query);
            using var doc = await GetJsonAsync($"{Base}/search/artist?q={q}&limit={limit}", ct);
            if (doc is not null
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in data.EnumerateArray())
                {
                    var id = a.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number
                        ? aid.GetInt64().ToString() : null;
                    var name = Str(a, "name");
                    if (id is null || string.IsNullOrWhiteSpace(name)) continue;

                    hits.Add(new ArtistHit(id, name, Str(a, "picture_xl") ?? Str(a, "picture_medium"), Int(a, "nb_album")));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer artist search '{Q}' failed: {M}", query, ex.Message);
        }

        return hits;
    }

    /// <summary>Every album from a Deezer artist id, used to populate an external
    /// artist's discography. Singles are filtered out the same way SearchAlbumsAsync
    /// does. This endpoint doesn't return a track count, so TrackCount is left 0 --
    /// GetAlbumDetailAsync fills the real number when the album is opened.</summary>
    public async Task<List<AlbumHit>> GetArtistAlbumsAsync(string deezerArtistId, int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deezerArtistId) || limit <= 0) return new List<AlbumHit>();
        var hits = new List<AlbumHit>();
        try
        {
            using var doc = await GetJsonAsync($"{Base}/artist/{deezerArtistId}/albums?limit={limit}", ct);
            if (doc is not null
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in data.EnumerateArray())
                {
                    var id = a.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number
                        ? aid.GetInt64().ToString() : null;
                    var title = Str(a, "title");
                    if (id is null || string.IsNullOrWhiteSpace(title)) continue;

                    var recordType = Str(a, "record_type");
                    if (string.Equals(recordType, "single", StringComparison.OrdinalIgnoreCase)) continue;

                    int? year = null;
                    var rd = Str(a, "release_date");
                    if (!string.IsNullOrEmpty(rd) && rd.Length >= 4 && int.TryParse(rd[..4], out var yr))
                        year = yr;

                    hits.Add(new AlbumHit(
                        id, title, "", // artist filled in by the caller, which already knows it
                        Str(a, "cover_xl") ?? Str(a, "cover_medium"),
                        year, 0, recordType));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer artist albums '{Id}' failed: {M}", deezerArtistId, ex.Message);
        }

        return hits;
    }

    /// <summary>An artist's most popular tracks (artist + title only -- callers resolve
    /// each pair into a real playable Song the same way external search results are
    /// built, so it gets a routing id, YouTube preview and star-to-download support).</summary>
    public async Task<List<(string Artist, string Title)>> GetArtistTopTrackNamesAsync(string deezerArtistId, int limit = 5, CancellationToken ct = default)
    {
        var names = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(deezerArtistId) || limit <= 0) return names;
        try
        {
            using var doc = await GetJsonAsync($"{Base}/artist/{deezerArtistId}/top?limit={limit}", ct);
            if (doc is not null
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in data.EnumerateArray())
                {
                    var title = Str(t, "title");
                    var artist = t.TryGetProperty("artist", out var art) ? Str(art, "name") : null;
                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                        names.Add((artist!, title!));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer artist top tracks '{Id}' failed: {M}", deezerArtistId, ex.Message);
        }

        return names;
    }

    /// <summary>Search the album catalog. Single-track "albums" are dropped: a plain
    /// artist query returns a lot of them and they crowd out real records.</summary>
    public async Task<List<AlbumHit>> SearchAlbumsAsync(string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new List<AlbumHit>();
        var hits = new List<AlbumHit>();
        try
        {
            var q = Uri.EscapeDataString(query);
            using var doc = await GetJsonAsync($"{Base}/search/album?q={q}&limit={limit}", ct);
            if (doc is not null
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                // Materialize everything before the JsonDocument is disposed.
                foreach (var a in data.EnumerateArray())
                {
                    var id = a.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number
                        ? aid.GetInt64().ToString() : null;
                    var title = Str(a, "title");
                    if (id is null || string.IsNullOrWhiteSpace(title)) continue;

                    var recordType = Str(a, "record_type");
                    var trackCount = Int(a, "nb_tracks") ?? 0;
                    if (string.Equals(recordType, "single", StringComparison.OrdinalIgnoreCase) && trackCount <= 2)
                        continue;

                    var artist = a.TryGetProperty("artist", out var art) ? Str(art, "name") : null;
                    hits.Add(new AlbumHit(
                        id, title, artist ?? "",
                        Str(a, "cover_xl") ?? Str(a, "cover_medium"),
                        null, trackCount, recordType));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer album search '{Q}' failed: {M}", query, ex.Message);
        }

        return hits;
    }

    /// <summary>Resolve an artist + album name to a Deezer album id. Needed because album
    /// ids minted from a song row carry no Deezer id, so the name is all we have.</summary>
    public async Task<string?> FindAlbumIdAsync(string? artist, string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(album)) return null;
        string? id = null;
        try
        {
            var q = Uri.EscapeDataString(
                string.IsNullOrWhiteSpace(artist) ? $"album:\"{album}\"" : $"artist:\"{artist}\" album:\"{album}\"");
            using var doc = await GetJsonAsync($"{Base}/search/album?q={q}&limit=1", ct);
            if (FirstData(doc) is JsonElement a
                && a.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number)
            {
                id = aid.GetInt64().ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer album id lookup '{A} - {Al}' failed: {M}", artist, album, ex.Message);
        }

        return id;
    }

    /// <summary>Album detail plus its full tracklist, ordered by disc then track position.
    /// One bounded request per resource; a release larger than the cap is reported as
    /// truncated rather than silently presented as complete.</summary>
    public async Task<AlbumDetail?> GetAlbumDetailAsync(string deezerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deezerId)) return null;
        AlbumDetail? detail = null;
        try
        {
            string title = "", artist = "", genre = "", label = "", cover = "";
            int? year = null;

            using (var doc = await GetJsonAsync($"{Base}/album/{deezerId}", ct))
            {
                if (doc is not null)
                {
                    var root = doc.RootElement;
                    title = Str(root, "title") ?? "";
                    cover = Str(root, "cover_xl") ?? Str(root, "cover_medium") ?? "";
                    label = Str(root, "label") ?? "";
                    var rd = Str(root, "release_date");
                    if (!string.IsNullOrEmpty(rd) && rd.Length >= 4 && int.TryParse(rd[..4], out var yr))
                        year = yr;
                    if (root.TryGetProperty("artist", out var art))
                        artist = Str(art, "name") ?? "";
                    if (root.TryGetProperty("genres", out var genres)
                        && genres.TryGetProperty("data", out var gd)
                        && gd.ValueKind == JsonValueKind.Array && gd.GetArrayLength() > 0)
                        genre = Str(gd[0], "name") ?? "";
                }
            }

            if (string.IsNullOrWhiteSpace(title)) return null;

            var tracks = new List<AlbumTrack>();
            using (var doc = await GetJsonAsync($"{Base}/album/{deezerId}/tracks?limit=300", ct))
            {
                if (doc is not null
                    && doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in data.EnumerateArray())
                    {
                        var tTitle = Str(t, "title");
                        if (string.IsNullOrWhiteSpace(tTitle)) continue;
                        var tArtist = t.TryGetProperty("artist", out var ta) ? Str(ta, "name") : null;
                        tracks.Add(new AlbumTrack(
                            tTitle, tArtist ?? artist, Int(t, "duration"),
                            Int(t, "track_position"), Int(t, "disk_number"), Str(t, "isrc")));
                    }

                    var total = Int(doc.RootElement, "total");
                    if (total is int n && n > tracks.Count)
                        _logger.LogWarning(
                            "deezer album '{Title}' ({Id}) returned {Got} of {Total} tracks; tracklist is truncated",
                            title, deezerId, tracks.Count, n);
                }
            }

            tracks = tracks
                .OrderBy(t => t.DiscNumber ?? 1)
                .ThenBy(t => t.TrackPosition ?? int.MaxValue)
                .ToList();

            detail = new AlbumDetail(deezerId, title, artist,
                string.IsNullOrEmpty(cover) ? null : cover, year,
                string.IsNullOrEmpty(genre) ? null : genre,
                string.IsNullOrEmpty(label) ? null : label,
                tracks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("deezer album detail {Id} failed: {M}", deezerId, ex.Message);
        }

        return detail;
    }

    private Task<int?> AlbumYearAsync(long albumId, CancellationToken ct)
    {
        // Shared across concurrent callers for the same album id (single-flight);
        // the entry is removed the instant it resolves, so this only collapses
        // simultaneous requests and never serves a stale result to a later search.
        return _albumYearTasks.GetOrAdd(albumId,
            id => new Lazy<Task<int?>>(() => FetchAlbumYearAsync(id))).Value;
    }

    private async Task<int?> FetchAlbumYearAsync(long albumId)
    {
        int? year = null;
        try
        {
            using var doc = await GetJsonAsync($"{Base}/album/{albumId}", CancellationToken.None);
            var rd = doc is null ? null : Str(doc.RootElement, "release_date");
            if (!string.IsNullOrEmpty(rd) && rd.Length >= 4 && int.TryParse(rd[..4], out var yr))
                year = yr;
        }
        catch { /* best-effort */ }
        _albumYearTasks.TryRemove(albumId, out _);
        return year;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await Client().GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var s = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(s);
    }

    private static JsonElement? FirstData(JsonDocument? doc)
    {
        if (doc is null) return null;
        if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
            return d[0];
        return null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;

}

using System.Collections.Concurrent;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Models.Download;
using Octo.Models.Search;
using Octo.Models.Subsonic;
using Octo.Services.Local;
using Octo.Services.Subsonic;
using TagLib;
using IOFile = System.IO.File;

namespace Octo.Services.Common;

/// <summary>
/// Abstract base class for download services.
/// Implements common download logic, tracking, and metadata writing.
/// Subclasses implement provider-specific download and authentication logic.
/// </summary>
public abstract class BaseDownloadService : IDownloadService
{
    protected readonly IConfiguration Configuration;
    protected readonly ILocalLibraryService LocalLibraryService;
    protected readonly IMusicMetadataService MetadataService;
    protected readonly SubsonicSettings SubsonicSettings;
    protected readonly ILogger Logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly NavidromeIdentityService _navIdentity;
    private readonly DownloadHistoryService _history;

    // The configured Library:DownloadPath. With auto-detect on this is only a
    // fallback used until Navidrome's real music folder is detected.
    private readonly string _configuredDownloadPath;

    /// <summary>
    /// Effective download destination. Resolves fresh each access so that once
    /// Navidrome's music folder is detected, downloads follow it without a restart.
    /// </summary>
    protected string DownloadPath => _navIdentity.EffectiveDownloadPath(_configuredDownloadPath);
    protected readonly string CachePath;
    
    // Concurrent because the album walk reads this WITHOUT holding DownloadLock while a
    // request thread can be writing to it under the lock. Every write used to be lock
    // guarded and the only unguarded reader was dead code; enabling album downloads makes
    // that pattern live, and concurrent read+write on Dictionary is undefined.
    protected readonly ConcurrentDictionary<string, DownloadInfo> ActiveDownloads = new();
    // Was new(1, 1) -- one download at a time system-wide regardless of available
    // bandwidth. Raised to allow several tracks (even across different albums) to
    // transfer concurrently; still bounded so a handful of slow/stalled peers can't
    // starve every other queued track.
    private const int MaxConcurrentDownloads = 6;
    protected readonly SemaphoreSlim DownloadLock = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    
    /// <summary>
    /// Lazy-loaded PlaylistSyncService to avoid circular dependency
    /// </summary>
    private PlaylistSyncService? _playlistSyncService;
    protected PlaylistSyncService? PlaylistSyncService
    {
        get
        {
            if (_playlistSyncService == null)
            {
                _playlistSyncService = _serviceProvider.GetService<PlaylistSyncService>();
            }
            return _playlistSyncService;
        }
    }
    
    /// <summary>
    /// Provider name (e.g., "deezer", "qobuz")
    /// </summary>
    protected abstract string ProviderName { get; }
    
    protected BaseDownloadService(
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        SubsonicSettings subsonicSettings,
        NavidromeIdentityService navIdentity,
        DownloadHistoryService history,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        Configuration = configuration;
        LocalLibraryService = localLibraryService;
        MetadataService = metadataService;
        SubsonicSettings = subsonicSettings;
        _navIdentity = navIdentity;
        _history = history;
        _serviceProvider = serviceProvider;
        Logger = logger;

        _configuredDownloadPath = configuration["Library:DownloadPath"] ?? "./downloads";
        CachePath = PathHelper.GetCachePath();

        if (!Directory.Exists(DownloadPath))
        {
            Directory.CreateDirectory(DownloadPath);
        }
        
        if (!Directory.Exists(CachePath))
        {
            Directory.CreateDirectory(CachePath);
        }
    }
    
    #region IDownloadService Implementation
    
    public async Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, cancellationToken);
    }
    
    public async Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var localPath = await DownloadSongInternalAsync(externalProvider, externalId, triggerAlbumDownload: true, cancellationToken);
        return IOFile.OpenRead(localPath);
    }
    
    public DownloadInfo? GetDownloadStatus(string songId)
    {
        ActiveDownloads.TryGetValue(songId, out var info);
        return info;
    }
    
    public async Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName)
        {
            return null;
        }
        
        // Check local library
        var localPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (localPath != null && IOFile.Exists(localPath))
        {
            return localPath;
        }
        
        // Check cache directory
        var cachedPath = GetCachedFilePath(externalProvider, externalId);
        if (cachedPath != null && IOFile.Exists(cachedPath))
        {
            return cachedPath;
        }
        
        return null;
    }
    
    public abstract Task<bool> IsAvailableAsync();
    
    /// <summary>
    /// Gets a direct stream from the provider CDN (true streaming, no disk).
    /// Default implementation returns null (not supported). Override in subclasses.
    /// </summary>
    public virtual Task<DirectStreamInfo?> GetDirectStreamAsync(string externalProvider, string externalId, string? rangeHeader = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<DirectStreamInfo?>(null);
    }
    
    public void DownloadRemainingAlbumTracksInBackground(string externalProvider, string albumExternalId, string excludeTrackExternalId)
    {
        if (externalProvider != ProviderName)
        {
            Logger.LogWarning("Provider '{Provider}' is not supported for album download", externalProvider);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadRemainingAlbumTracksAsync(albumExternalId, excludeTrackExternalId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to download remaining album tracks for album {AlbumId}", albumExternalId);
            }
        });
    }
    
    #endregion
    
    #region Template Methods (to be implemented by subclasses)
    
    /// <summary>
    /// Downloads a track and saves it to disk.
    /// Subclasses implement provider-specific logic (encryption, authentication, etc.)
    /// </summary>
    /// <param name="trackId">External track ID</param>
    /// <param name="song">Song metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Local file path where the track was saved</returns>
    protected abstract Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken);

    /// <summary>Record a completed download in the fetched-songs log. Best-effort:
    /// format + source are derived from the file extension (flac -> Soulseek/lossless,
    /// otherwise -> YouTube/lossy), which matches Octo's two download sources.</summary>
    private async Task RecordHistoryAsync(Song song, string localPath)
    {
        try
        {
            string? cover = song.CoverArtUrlLarge ?? song.CoverArtUrl;
            string? album = string.IsNullOrEmpty(song.Album) ? null : song.Album;

            // The star path rebuilds the song from its id, so it has no artwork or
            // album. Pull the cover + album straight from Deezer for the log entry
            // (cached, so this is cheap). Best-effort — never fails a download.
            if (string.IsNullOrEmpty(cover) || album is null)
            {
                try
                {
                    var deezer = _serviceProvider.GetService<Octo.Services.Metadata.DeezerMetadataService>();
                    if (deezer != null)
                    {
                        var meta = await deezer.EnrichTrackAsync(song.Artist, song.Title, includeYear: false);
                        if (meta != null)
                        {
                            cover ??= meta.AlbumCoverUrl;
                            album ??= meta.AlbumTitle;
                        }
                    }
                }
                catch { /* enrichment is best-effort */ }
            }

            var ext = System.IO.Path.GetExtension(localPath).TrimStart('.').ToUpperInvariant();
            long size = 0;
            try { size = new FileInfo(localPath).Length; } catch { /* best-effort */ }
            _history.Record(new DownloadHistoryEntry
            {
                Artist = song.Artist,
                Title = song.Title,
                Album = album ?? string.Empty,
                Path = localPath,
                Format = string.IsNullOrEmpty(ext) ? "?" : ext,
                Source = ext == "FLAC" ? "Soulseek" : "YouTube",
                CoverArtUrl = cover,
                SizeBytes = size,
                DownloadedAt = DateTime.UtcNow.ToString("o"),
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to record download history for {Path}", localPath);
        }
    }

    /// <summary>
    /// Extracts the external album ID from the internal album ID format.
    /// Example: "ext-deezer-album-123456" -> "123456"
    /// </summary>
    protected abstract string? ExtractExternalIdFromAlbumId(string albumId);

    /// <summary>
    /// Re-assert any routing state a long-running batch depends on. An album download can
    /// run for hours while searches keep filling the id registry, so a track's routing can
    /// be evicted before its turn comes. Ids are a pure hash of the routing fields, so
    /// re-registering the SAME fields restores the SAME id and is idempotent.
    /// </summary>
    protected virtual void EnsureRoutingRegistered(Song track) { }
    
    #endregion
    
    #region Common Download Logic
    
    /// <summary>
    /// Internal method for downloading a song with control over album download triggering
    /// </summary>
    /// <param name="forcePermanent">
    /// Treat this as a permanent download regardless of Cache storage mode. Cache mode
    /// otherwise skips library registration, the fetched-songs log and the rescan, which
    /// is wrong for a deliberate "keep this" gesture like hearting an album.
    /// </param>
    protected async Task<string> DownloadSongInternalAsync(string externalProvider, string externalId, bool triggerAlbumDownload, CancellationToken cancellationToken = default, bool forcePermanent = false)
    {
        if (externalProvider != ProviderName)
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        var songId = $"ext-{externalProvider}-{externalId}";
        var isCache = !forcePermanent && SubsonicSettings.StorageMode == StorageMode.Cache;
        
        // Acquire lock BEFORE checking existence to prevent race conditions with concurrent requests
        await DownloadLock.WaitAsync(cancellationToken);
        // Tracks whether we still owe a Release. The wait-for-in-progress branch below
        // hands the permit back early so it doesn't hold a download slot while idling,
        // and without this flag the finally would release a SECOND time — inflating the
        // semaphore past MaxConcurrentDownloads (or throwing SemaphoreFullException when
        // the pool is idle). Every exit path goes through the finally, including the
        // early return and throw inside that branch.
        var holdsDownloadLock = true;
        // Whether THIS call created the ActiveDownloads entry for songId. A call that
        // only waited on someone else's in-progress download must not report failure
        // on their behalf: doing so flips a healthy, still-running download to Failed,
        // which then makes every other waiter bail out too while the real download
        // goes on to succeed.
        var ownsDownloadInfo = false;

        try
        {
            // Check if already downloaded (skip for cache mode as we want to check cache folder)
            if (!isCache)
            {
                var existingPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
                if (existingPath != null && IOFile.Exists(existingPath))
                {
                    Logger.LogInformation("Song already downloaded: {Path}", existingPath);
                    return existingPath;
                }
            }
            else
            {
                // For cache mode, check if file exists in cache directory
                var cachedPath = GetCachedFilePath(externalProvider, externalId);
                if (cachedPath != null && IOFile.Exists(cachedPath))
                {
                    Logger.LogInformation("Song found in cache: {Path}", cachedPath);
                    // Update file access time for cache cleanup logic
                    IOFile.SetLastAccessTime(cachedPath, DateTime.UtcNow);
                    return cachedPath;
                }
            }

            // Check if download in progress
            if (ActiveDownloads.TryGetValue(songId, out var activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
            {
                Logger.LogInformation("Download already in progress for {SongId}, waiting...", songId);
                // Release lock while waiting — don't occupy a download slot just to poll.
                // Clear the flag FIRST: if Release() itself throws, the finally must not
                // try to release a second time and compound the problem.
                holdsDownloadLock = false;
                DownloadLock.Release();

                // Bounded wait. A peer that accepts the transfer and then stalls leaves
                // the owning download InProgress indefinitely; without a cap every
                // duplicate request for that track would poll forever, and callers
                // (a client waiting on /rest/stream) would hang with it.
                var waitedMs = 0;
                const int pollIntervalMs = 500;
                const int maxWaitMs = 10 * 60 * 1000;
                while (ActiveDownloads.TryGetValue(songId, out activeDownload)
                       && activeDownload.Status == DownloadStatus.InProgress)
                {
                    if (waitedMs >= maxWaitMs)
                    {
                        throw new TimeoutException(
                            $"Timed out after {maxWaitMs / 1000}s waiting on an in-progress download of {songId}");
                    }
                    await Task.Delay(pollIntervalMs, cancellationToken);
                    waitedMs += pollIntervalMs;
                }

                if (activeDownload?.Status == DownloadStatus.Completed && activeDownload.LocalPath != null)
                {
                    return activeDownload.LocalPath;
                }

                throw new Exception(activeDownload?.ErrorMessage ?? "Download failed");
            }

            // Get metadata
            // In Album mode, fetch the full album first to ensure AlbumArtist is correctly set
            Song? song = null;
            
            if (SubsonicSettings.DownloadMode == DownloadMode.Album)
            {
                // First try to get the song to extract album ID
                var tempSong = await MetadataService.GetSongAsync(externalProvider, externalId);
                if (tempSong != null && !string.IsNullOrEmpty(tempSong.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(tempSong.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        // Get full album with correct AlbumArtist
                        var album = await MetadataService.GetAlbumAsync(externalProvider, albumExternalId);
                        if (album != null)
                        {
                            // Find the track in the album
                            song = album.Songs.FirstOrDefault(s => s.ExternalId == externalId);
                        }
                    }
                }
            }
            
            // Fallback to individual song fetch if not in Album mode or album fetch failed
            if (song == null)
            {
                song = await MetadataService.GetSongAsync(externalProvider, externalId);
            }
            
            if (song == null)
            {
                throw new Exception("Song not found");
            }

            var downloadInfo = new DownloadInfo
            {
                SongId = songId,
                ExternalId = externalId,
                ExternalProvider = externalProvider,
                Status = DownloadStatus.InProgress,
                StartedAt = DateTime.UtcNow
            };
            ActiveDownloads[songId] = downloadInfo;
            // From here on this call OWNS the ActiveDownloads entry for songId, and is
            // the only one allowed to mark it Failed. A concurrent caller that merely
            // waited on this download must never write to it — see the catch below.
            ownsDownloadInfo = true;

            var localPath = await DownloadTrackAsync(externalId, song, cancellationToken);
            
            downloadInfo.Status = DownloadStatus.Completed;
            downloadInfo.LocalPath = localPath;
            downloadInfo.CompletedAt = DateTime.UtcNow;
            
            song.LocalPath = localPath;

            // Enrich from Deezer and write rich tags + real album art onto the file.
            // Downloads otherwise arrive bare (YouTube: artist/title + a video
            // thumbnail; Soulseek: whatever the peer tagged), so this is what makes
            // every fetched song a properly-tagged library citizen.
            await EnrichAndTagAsync(song, localPath, cancellationToken);

            // Check if this track belongs to a playlist and update M3U
            if (PlaylistSyncService != null)
            {
                try
                {
                    var playlistId = PlaylistSyncService.GetPlaylistIdForTrack(songId);
                    if (playlistId != null)
                    {
                        Logger.LogInformation("Track {SongId} belongs to playlist {PlaylistId}, adding to M3U", songId, playlistId);
                        await PlaylistSyncService.AddTrackToM3UAsync(playlistId, song, localPath, isFullPlaylistDownload: false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to update playlist M3U for track {SongId}", songId);
                }
            }
            
            // Only register and scan if NOT in cache mode
            if (!isCache)
            {
                await LocalLibraryService.RegisterDownloadedSongAsync(song, localPath);
                await RecordHistoryAsync(song, localPath);

                // Trigger a Subsonic library rescan (with debounce)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LocalLibraryService.TriggerLibraryScanAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to trigger library scan after download");
                    }
                });
                
                // If download mode is Album and triggering is enabled, start background download of remaining tracks
                if (triggerAlbumDownload && SubsonicSettings.DownloadMode == DownloadMode.Album && !string.IsNullOrEmpty(song.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(song.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        Logger.LogInformation("Download mode is Album, triggering background download for album {AlbumId}", albumExternalId);
                        DownloadRemainingAlbumTracksInBackground(externalProvider, albumExternalId, externalId);
                    }
                }
            }
            else
            {
                Logger.LogInformation("Cache mode: skipping library registration and scan");
            }
            
            Logger.LogInformation("Download completed: {Path}", localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            // Only the owner reports failure. Without this guard a waiter that gave up
            // (timeout, or the owner already failed) would stamp Failed onto a record
            // it doesn't own — cascading a single slow download into failures for
            // every concurrent request for the same track.
            if (ownsDownloadInfo && ActiveDownloads.TryGetValue(songId, out var downloadInfo))
            {
                downloadInfo.Status = DownloadStatus.Failed;
                downloadInfo.ErrorMessage = ex.Message;
            }
            Logger.LogError(ex, "Download failed for {SongId}", songId);
            throw;
        }
        finally
        {
            if (holdsDownloadLock) DownloadLock.Release();
        }
    }
    
    protected async Task DownloadRemainingAlbumTracksAsync(string albumExternalId, string excludeTrackExternalId)
    {
        Logger.LogInformation("Starting background download for album {AlbumId} (excluding track {TrackId})", 
            albumExternalId, excludeTrackExternalId);

        // Resolving an album's tracklist crosses the network (Deezer), so a blip
        // returns a valid Album object carrying zero songs. Untreated, that reads
        // as "album fully downloaded" and the star silently does nothing — the
        // user's only clue is that no files appear, and re-starring is the only
        // recovery. Retry a few times before believing an empty tracklist.
        const int maxResolveAttempts = 3;
        Album? album = null;
        for (var attempt = 1; attempt <= maxResolveAttempts; attempt++)
        {
            album = await MetadataService.GetAlbumAsync(ProviderName, albumExternalId);
            if (album is { Songs.Count: > 0 }) break;

            if (attempt < maxResolveAttempts)
            {
                Logger.LogWarning(
                    "Album {AlbumId} resolved to {Count} track(s) on attempt {N}/{Max}; retrying",
                    albumExternalId, album?.Songs.Count ?? 0, attempt, maxResolveAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }

        if (album == null)
        {
            Logger.LogWarning("Album {AlbumId} not found, cannot download remaining tracks", albumExternalId);
            return;
        }

        if (album.Songs.Count == 0)
        {
            // Loud, because this is indistinguishable from success in every other log line.
            Logger.LogError(
                "Album '{Title}' ({AlbumId}) has an empty tracklist after {Max} attempts — nothing will be " +
                "downloaded. This is usually a transient metadata-provider failure; star the album again to retry.",
                album.Title, albumExternalId, maxResolveAttempts);
            return;
        }

        var tracksToDownload = album.Songs
            .Where(s => s.ExternalId != excludeTrackExternalId && !string.IsNullOrEmpty(s.ExternalId))
            .ToList();

        Logger.LogInformation("Found {Count} additional tracks to download for album '{AlbumTitle}'", 
            tracksToDownload.Count, album.Title);

        // Counted, because "every track failed" and "every track succeeded" used to produce
        // an identical "Completed background download" line — an album that downloaded
        // nothing at all looked exactly like one that downloaded perfectly.
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        const int consecutiveFailureAbort = 5;
        var consecutiveFailures = 0;

        foreach (var track in tracksToDownload)
        {
            if (consecutiveFailures >= consecutiveFailureAbort && succeeded == 0)
            {
                Logger.LogError(
                    "Aborting album '{AlbumTitle}': the first {Count} tracks all failed to download. "
                    + "The downloader is likely offline or rejecting requests — fix that, then star the album again.",
                    album.Title, consecutiveFailures);
                failed += tracksToDownload.Count - (succeeded + failed + skipped);
                break;
            }

            try
            {
                EnsureRoutingRegistered(track);

                var existingPath = await LocalLibraryService.GetLocalPathForExternalSongAsync(ProviderName, track.ExternalId!);
                if (existingPath != null && IOFile.Exists(existingPath))
                {
                    Logger.LogDebug("Track {TrackId} already downloaded, skipping", track.ExternalId);
                    skipped++;
                    continue;
                }

                // Check if download is already in progress or recently completed
                var songId = $"ext-{ProviderName}-{track.ExternalId}";
                if (ActiveDownloads.TryGetValue(songId, out var activeDownload))
                {
                    if (activeDownload.Status == DownloadStatus.InProgress)
                    {
                        Logger.LogDebug("Track {TrackId} download already in progress, skipping", track.ExternalId);
                        skipped++;
                        continue;
                    }
                    
                    if (activeDownload.Status == DownloadStatus.Completed)
                    {
                        Logger.LogDebug("Track {TrackId} already downloaded in this session, skipping", track.ExternalId);
                        skipped++;
                        continue;
                    }
                }

                Logger.LogInformation("Downloading track '{Title}' from album '{Album}'", track.Title, album.Title);
                var downloadedPath = await DownloadSongInternalAsync(ProviderName, track.ExternalId!, triggerAlbumDownload: false, CancellationToken.None, forcePermanent: true);

                if (string.IsNullOrEmpty(downloadedPath))
                {
                    failed++;
                    consecutiveFailures++;
                    Logger.LogWarning("Track {TrackId} '{Title}' produced no file", track.ExternalId, track.Title);
                    continue;
                }

                succeeded++;
                consecutiveFailures = 0;

                // Force a rescan per track so the album fills in progressively in the
                // client instead of appearing all at once at the end. The per-download
                // scan inside DownloadSongInternalAsync is debounced, which during a
                // batch swallows most triggers and can strand the final tracks entirely.
                await LocalLibraryService.TriggerLibraryScanAsync(force: true);
            }
            catch (Exception ex)
            {
                failed++;
                consecutiveFailures++;
                Logger.LogWarning(ex, "Failed to download track {TrackId} '{Title}'", track.ExternalId, track.Title);
            }
        }

        if (succeeded == 0 && failed > 0)
        {
            // Nothing new landed and something went wrong, so there is nothing worth
            // normalizing or rescanning — and this must not read as success.
            Logger.LogError(
                "Album '{AlbumTitle}' downloaded 0 of {Total} tracks ({Failed} failed, {Skipped} already present). "
                + "Nothing was added to the library.",
                album.Title, tracksToDownload.Count, failed, skipped);
            return;
        }

        // Final consistency pass: strip every file in the album's folder and rewrite
        // Album/AlbumArtist/Year/Genre/cover art from ONE canonical Deezer album lookup
        // instead of relying on each track's own (frequently disagreeing) per-track
        // lookup. This is what actually guarantees every track groups as a single album
        // in Navidrome — two earlier attempts at per-track agreement (clearing leftover
        // MusicBrainz/sort tags, then a shared in-memory Year cache) both still let
        // individual tracks diverge on Year because each one's own Deezer match could
        // differ. Runs on the whole folder, not just this batch, so it also normalizes
        // the one track that triggered the album download in the first place.
        try
        {
            var albumArtist = !string.IsNullOrEmpty(album.Artist)
                ? album.Artist
                : tracksToDownload.FirstOrDefault()?.AlbumArtist ?? tracksToDownload.FirstOrDefault()?.Artist ?? "";
            var albumFolder = Path.Combine(
                DownloadPath,
                PathHelper.SanitizeFolderName(albumArtist),
                PathHelper.SanitizeFolderName(album.Title));
            await NormalizeAlbumMetadataAsync(albumFolder, albumArtist, album.Title, CancellationToken.None);
            await LocalLibraryService.TriggerLibraryScanAsync(force: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Album metadata normalization failed for '{AlbumTitle}'", album.Title);
        }

        if (failed > 0)
        {
            Logger.LogWarning(
                "Album '{AlbumTitle}' finished incomplete: {Succeeded} downloaded, {Failed} failed, "
                + "{Skipped} already present, of {Total} tracks. Re-star it to retry the missing tracks.",
                album.Title, succeeded, failed, skipped, tracksToDownload.Count);
        }
        else
        {
            Logger.LogInformation(
                "Completed background download for album '{AlbumTitle}': {Succeeded} downloaded, "
                + "{Skipped} already present, of {Total} tracks",
                album.Title, succeeded, skipped, tracksToDownload.Count);
        }
    }

    /// <summary>
    /// One-time final pass over a completed album folder: resolves Album/AlbumArtist/
    /// Year/Genre/cover art ONCE from a single Deezer album lookup (not per track),
    /// strips every existing tag from every file in the folder, then rewrites those
    /// shared album-level fields identically everywhere, preserving only each file's
    /// own per-track fields (title, artist, track/disc number, composer, ISRC comment).
    /// Best-effort throughout: a missed Deezer match or a single file's write failure
    /// just leaves that file with whatever tags its own download already gave it.
    /// </summary>
    protected async Task NormalizeAlbumMetadataAsync(string albumFolder, string fallbackArtist, string fallbackAlbum, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(albumFolder))
        {
            Logger.LogWarning("Album normalize: folder not found, skipping: {Folder}", albumFolder);
            return;
        }

        var files = Directory.EnumerateFiles(albumFolder)
            .Where(f => f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            Logger.LogWarning("Album normalize: no audio files found in {Folder}", albumFolder);
            return;
        }

        var deezer = _serviceProvider.GetService<Octo.Services.Metadata.DeezerMetadataService>();
        if (deezer == null) return;

        var albumId = await deezer.FindAlbumIdAsync(fallbackArtist, fallbackAlbum, cancellationToken);
        if (string.IsNullOrEmpty(albumId))
        {
            Logger.LogWarning(
                "Album normalize: no Deezer match for '{Artist} - {Album}', leaving per-track tags as-is",
                fallbackArtist, fallbackAlbum);
            return;
        }

        var detail = await deezer.GetAlbumDetailAsync(albumId, cancellationToken);
        if (detail == null) return;

        byte[]? coverData = !string.IsNullOrEmpty(detail.CoverUrl)
            ? await DownloadCoverArtAsync(detail.CoverUrl, cancellationToken)
            : null;

        Logger.LogInformation(
            "Album normalize: '{Album}' by {Artist} -> Year={Year} Genre={Genre}, applying to {Count} files",
            detail.Title, detail.Artist, detail.Year, detail.Genre, files.Count);

        foreach (var file in files)
        {
            try
            {
                using var tagFile = TagLib.File.Create(file);

                // Preserve per-track identity before wiping everything else.
                var title = tagFile.Tag.Title;
                var performers = tagFile.Tag.Performers;
                var track = tagFile.Tag.Track;
                var trackCount = tagFile.Tag.TrackCount;
                var disc = tagFile.Tag.Disc;
                var composers = tagFile.Tag.Composers;
                var comment = tagFile.Tag.Comment;

                StripAllTags(tagFile);

                if (!string.IsNullOrEmpty(title)) tagFile.Tag.Title = title;
                if (performers is { Length: > 0 }) tagFile.Tag.Performers = performers;
                if (track > 0) tagFile.Tag.Track = track;
                if (trackCount > 0) tagFile.Tag.TrackCount = trackCount;
                if (disc > 0) tagFile.Tag.Disc = disc;
                if (composers is { Length: > 0 }) tagFile.Tag.Composers = composers;
                if (!string.IsNullOrEmpty(comment)) tagFile.Tag.Comment = comment;

                // Shared album-level fields: identical on every file, from one lookup.
                tagFile.Tag.Album = detail.Title;
                tagFile.Tag.AlbumArtists = new[] { detail.Artist };
                tagFile.Tag.Year = detail.Year.HasValue ? (uint)detail.Year.Value : 0;
                if (!string.IsNullOrEmpty(detail.Genre)) tagFile.Tag.Genres = new[] { detail.Genre };

                if (coverData is { Length: > 0 })
                {
                    tagFile.Tag.Pictures = new TagLib.IPicture[]
                    {
                        new TagLib.Picture
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = "image/jpeg",
                            Description = "Cover",
                            Data = new TagLib.ByteVector(coverData)
                        }
                    };
                }

                tagFile.Save();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Album normalize: failed to rewrite tags for {File}", file);
            }
        }

        Logger.LogInformation("Album normalize: completed consistency pass for '{Album}'", detail.Title);
    }

    /// <summary>
    /// Remove every tag a file carries, so what gets written afterwards is the
    /// complete contents rather than a partial overlay on someone else's rip.
    ///
    /// <c>Tag.Clear()</c> alone is NOT enough on FLAC. It clears the properties
    /// TagLib models and leaves every unmodelled Xiph comment in place — BARCODE,
    /// RELEASECOUNTRY, MUSICBRAINZ_ALBUMSTATUS, ALBUMARTISTS_SORT, and most
    /// damagingly MUSICBRAINZ_ALBUMCOMMENT, which Navidrome reads as an album
    /// "version". Two tracks of a 15-track album carrying ALBUMCOMMENT=deluxe
    /// while the other 13 did not split it into "ZABA (Deluxe)" (13 tracks) and
    /// "ZABA (Deluxe) (deluxe)" (2) even though album, artist, year and genre
    /// were byte-identical across all fifteen.
    ///
    /// Rips from different sharers never agree on these extras, so anything left
    /// behind is a fragmentation risk. Wipe the lot and rebuild from one lookup.
    /// </summary>
    private static void StripAllTags(TagLib.File tagFile)
    {
        tagFile.Tag.Clear();

        if (tagFile.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            // Materialize first: RemoveField mutates the collection being enumerated.
            foreach (var field in System.Linq.Enumerable.ToArray(xiph))
                xiph.RemoveField(field);

            // A FLAC can also carry legacy ID3/APE blocks that Navidrome may read in
            // preference to Xiph. Only safe to drop wholesale because Xiph is this
            // file's primary tag — doing it on an MP3 would delete what we're about
            // to write.
            tagFile.RemoveTags(TagTypes.Id3v1 | TagTypes.Id3v2 | TagTypes.Ape);
        }
    }

    #endregion
    
    #region Common Metadata Writing
    
    /// <summary>
    /// Writes ID3/Vorbis metadata and cover art to the audio file
    /// </summary>
    /// <summary>
    /// Fills any missing metadata on <paramref name="song"/> from Deezer, then writes
    /// full tags + real album art onto the downloaded file. Existing values win (a
    /// well-tagged Soulseek FLAC is enriched, not overwritten); Deezer fills the gaps
    /// and supplies the cover. Best-effort — a miss or write failure never breaks the
    /// download, the file just keeps whatever tags it already had.
    /// </summary>
    protected async Task EnrichAndTagAsync(Song song, string filePath, CancellationToken cancellationToken)
    {
        // Last.fm/YouTube titles often carry a redundant "Artist - " prefix (e.g.
        // "Radiohead - No Surprises") which both mislabels the file and breaks the
        // Deezer lookup. Strip it for the written title; strip bracketed junk too for
        // the lookup query so the match lands and we get real album art + tags.
        song.Title = StripArtistPrefix(song.Artist, song.Title);
        var queryTitle = StripBracketedJunk(song.Title);

        try
        {
            var deezer = _serviceProvider.GetService<Octo.Services.Metadata.DeezerMetadataService>();
            if (deezer != null)
            {
                var m = await deezer.EnrichTrackFullAsync(song.Artist, queryTitle, cancellationToken);
                if (m != null)
                {
                    if (string.IsNullOrEmpty(song.Album) && !string.IsNullOrEmpty(m.AlbumTitle)) song.Album = m.AlbumTitle;
                    if (string.IsNullOrEmpty(song.AlbumArtist) && !string.IsNullOrEmpty(m.ArtistName)) song.AlbumArtist = m.ArtistName;
                    if (string.IsNullOrEmpty(song.CoverArtUrlLarge)) song.CoverArtUrlLarge = m.AlbumCoverUrl;
                    if (!song.Year.HasValue) song.Year = m.Year;
                    if (!song.Track.HasValue) song.Track = m.TrackNumber;
                    if (!song.DiscNumber.HasValue) song.DiscNumber = m.DiscNumber;
                    if (!song.TotalTracks.HasValue) song.TotalTracks = m.TotalTracks;
                    if (!song.Duration.HasValue) song.Duration = m.Duration;
                    if (string.IsNullOrEmpty(song.Genre)) song.Genre = m.Genre;
                    if (string.IsNullOrEmpty(song.Isrc)) song.Isrc = m.Isrc;
                    if (string.IsNullOrEmpty(song.Label)) song.Label = m.Label;
                    if (string.IsNullOrEmpty(song.ReleaseDate)) song.ReleaseDate = m.ReleaseDate;
                }
                else
                {
                    Logger.LogWarning("Deezer had no match for '{Artist} - {Title}'; if the file's own Album/AlbumArtist tags are wrong they will be left as-is", song.Artist, queryTitle);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Deezer enrichment for tagging failed for '{Artist} - {Title}'", song.Artist, song.Title);
        }

        await WriteMetadataAsync(filePath, song, cancellationToken);
    }

    /// <summary>Drop a redundant leading "Artist - " from a track title.</summary>
    private static string StripArtistPrefix(string? artist, string? title)
    {
        var t = (title ?? string.Empty).Trim();
        var a = (artist ?? string.Empty).Trim();
        if (a.Length > 0 && t.StartsWith(a + " - ", StringComparison.OrdinalIgnoreCase))
            t = t[(a.Length + 3)..].Trim();
        return t;
    }

    /// <summary>Strip [bracketed] / (parenthesized) annotations for a cleaner Deezer
    /// query (e.g. "No Surprises (Official Video)" -> "No Surprises"). Only used for
    /// the lookup, not the written title, so real "(feat. …)" tags are preserved.</summary>
    private static string StripBracketedJunk(string title)
    {
        var stripped = System.Text.RegularExpressions.Regex
            .Replace(title ?? string.Empty, @"\s*[\[\(][^\]\)]*[\]\)]", "").Trim();
        // A title that is entirely an annotation ("(Exchange)") strips to nothing, which
        // would send an empty query to Deezer. Fall back to the original.
        return stripped.Length == 0 ? (title ?? string.Empty).Trim() : stripped;
    }

    protected async Task WriteMetadataAsync(string filePath, Song song, CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogInformation("Writing metadata to: {Path}", filePath);
            
            using var tagFile = TagLib.File.Create(filePath);
            
            // Basic metadata. Title/artist we always have; only overwrite album +
            // album-artist when we actually resolved them, so a well-tagged Soulseek
            // FLAC keeps its own album if Deezer had no match.
            if (!string.IsNullOrEmpty(song.Title)) tagFile.Tag.Title = song.Title;
            if (!string.IsNullOrEmpty(song.Artist)) tagFile.Tag.Performers = new[] { song.Artist };
            if (!string.IsNullOrEmpty(song.Album))
            {
                tagFile.Tag.Album = song.Album;
                // Soulseek uploaders often mix tracks pulled from different releases
                // (standard vs. deluxe editions, or untagged rips) into one shared
                // folder. Each file can carry its own embedded MusicBrainz release
                // id from wherever it originally came from, and Navidrome groups
                // albums by that id when present — so a single album we organized
                // into one folder can still fragment into two or three "albums" in
                // the client. Since we're the ones assigning the canonical Album
                // name here, clear the leftover per-file release ids too so every
                // track in the album groups the same way (by name + album artist).
                // Isolated in its own try/catch: this block only clears secondary
                // grouping fields (MusicBrainz ids, *SORT tags), it's not essential
                // like Title/Artist/Album above. A prior bug here (Xiph MusicBrainz
                // setters throwing NullReferenceException on a literal null — fixed
                // by using "" instead) silently discarded Title/Artist/Album/Save()
                // too, because the whole method shared one outer try/catch: one
                // field throwing meant NOTHING got written and the file kept
                // whatever wrong tags a Soulseek peer originally baked in (this is
                // what let the "Awaken, My Love!" tracks keep a totally unrelated
                // peer's Album/Artist tags). Now a failure here only skips these
                // secondary fields instead of aborting the whole write.
                try
                {
                    tagFile.Tag.MusicBrainzReleaseId = "";
                    tagFile.Tag.MusicBrainzReleaseGroupId = "";
                    tagFile.Tag.MusicBrainzReleaseArtistId = "";
                    // Same story for *SORT fields: Navidrome's album identity also factors
                    // in the album-artist sort name, and a Soulseek upload mixing releases
                    // means some files carry ARTISTSORT/ALBUMARTISTSORT and others don't —
                    // that mismatch alone is enough to split one album into two. Clear the
                    // leftover sort tags so every track in the album agrees (has none).
                    tagFile.Tag.PerformersSort = Array.Empty<string>();
                    tagFile.Tag.AlbumArtistsSort = Array.Empty<string>();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to clear MusicBrainz/sort tags on {Path}; Title/Artist/Album were still written", filePath);
                }
            }
            if (!string.IsNullOrEmpty(song.AlbumArtist))
                tagFile.Tag.AlbumArtists = new[] { song.AlbumArtist };
            else if (!string.IsNullOrEmpty(song.Artist))
                tagFile.Tag.AlbumArtists = new[] { song.Artist };

            // Only write the track number when we actually have one, and only pair
            // the total with it — avoids a bogus "0/11" when Deezer's search result
            // carried the album total but not this track's position.
            if (song.Track is > 0)
            {
                tagFile.Tag.Track = (uint)song.Track.Value;
                if (song.TotalTracks.HasValue)
                    tagFile.Tag.TrackCount = (uint)song.TotalTracks.Value;
            }

            if (song.DiscNumber.HasValue)
                tagFile.Tag.Disc = (uint)song.DiscNumber.Value;

            // Year, both cases in one place so there's only ever one line to change.
            // Within an album we normalize to "the value we resolved, or none at all"
            // (0 clears the tag) so tracks in a folder can't disagree — presence vs.
            // absence alone is enough to split an album in Navidrome. This is only a
            // best-effort first pass, though: NormalizeAlbumMetadataAsync re-stamps
            // Year identically across the finished album from ONE canonical lookup,
            // which is what actually guarantees agreement. For a standalone single
            // there's no sibling track to agree with, so we only write a year we
            // actually have and otherwise leave the peer's own tag untouched.
            if (!string.IsNullOrEmpty(song.Album))
                tagFile.Tag.Year = song.Year.HasValue ? (uint)song.Year.Value : 0;
            else if (song.Year.HasValue)
                tagFile.Tag.Year = (uint)song.Year.Value;

            if (!string.IsNullOrEmpty(song.Genre))
                tagFile.Tag.Genres = new[] { song.Genre };
            
            if (song.Bpm.HasValue)
                tagFile.Tag.BeatsPerMinute = (uint)song.Bpm.Value;
            
            if (song.Contributors.Count > 0)
                tagFile.Tag.Composers = song.Contributors.ToArray();
            
            if (!string.IsNullOrEmpty(song.Copyright))
                tagFile.Tag.Copyright = song.Copyright;
            
            var comments = new List<string>();
            if (!string.IsNullOrEmpty(song.Isrc))
                comments.Add($"ISRC: {song.Isrc}");
            
            if (comments.Count > 0)
                tagFile.Tag.Comment = string.Join(" | ", comments);
            
            // Download and embed cover art
            var coverUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl;
            if (!string.IsNullOrEmpty(coverUrl))
            {
                try
                {
                    var coverData = await DownloadCoverArtAsync(coverUrl, cancellationToken);
                    if (coverData != null && coverData.Length > 0)
                    {
                        var mimeType = coverUrl.Contains(".png") ? "image/png" : "image/jpeg";
                        var picture = new TagLib.Picture
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = mimeType,
                            Description = "Cover",
                            Data = new TagLib.ByteVector(coverData)
                        };
                        tagFile.Tag.Pictures = new TagLib.IPicture[] { picture };
                        Logger.LogInformation("Cover art embedded: {Size} bytes", coverData.Length);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to download cover art from {Url}", coverUrl);
                }
            }
            
            tagFile.Save();
            Logger.LogInformation("Metadata written successfully to: {Path}", filePath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to write metadata to: {Path}", filePath);
        }
    }
    
    /// <summary>
    /// Downloads cover art from a URL
    /// </summary>
    protected async Task<byte[]?> DownloadCoverArtAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            // Factory-created, not `new HttpClient()`: this runs once per downloaded
            // track (and again per album normalize pass), so a fresh handler each
            // time would pile up sockets in TIME_WAIT across a large album batch.
            var httpClient = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
            using var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to download cover art from {Url}", url);
            return null;
        }
    }
    
    #endregion
    
    #region Utility Methods
    
    /// <summary>
    /// Ensures a directory exists, creating it and all parent directories if necessary
    /// </summary>
    protected void EnsureDirectoryExists(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                Logger.LogDebug("Created directory: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create directory: {Path}", path);
            throw;
        }
    }
    
    /// <summary>
    /// Gets the cached file path for a given provider and external ID
    /// Returns null if no cached file exists
    /// </summary>
    protected string? GetCachedFilePath(string provider, string externalId)
    {
        try
        {
            // Search for cached files matching the pattern: {provider}_{externalId}.*
            var pattern = $"{provider}_{externalId}.*";
            var files = Directory.GetFiles(CachePath, pattern, SearchOption.AllDirectories);
            
            if (files.Length > 0)
            {
                return files[0]; // Return first match
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to search for cached file: {Provider}_{ExternalId}", provider, externalId);
            return null;
        }
    }
    
    #endregion
}

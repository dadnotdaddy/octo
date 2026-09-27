"""
yt-dlp shim — a tiny Flask service that wraps yt-dlp behind two endpoints.

Why this is a separate container instead of in-process inside Octo:
  Spawning yt-dlp from .NET via System.Diagnostics.Process inside an LXC
  produced wedge conditions on client cancellation (the LXC's namespace
  state would lock up). Isolating yt-dlp here means Octo only ever talks
  HTTP to this service, and any process-management quirks stay sandboxed.

Endpoints:
  GET /search?q=<query>[&duration=<sec>]
      One yt-dlp extraction returning {video_id, title, duration, channel}.
      Without a duration hint it also resolves and caches the stream URL in
      the SAME extraction, so a later /stream is a deterministic cache hit.
      With a hint it picks the closest-length of several flat candidates
      (cheap, no per-video extraction), then resolves the winner's URL.

  GET /stream?id=<videoId>
      Resolves the best-audio URL (cached), then proxies bytes from
      YouTube's CDN to the caller. Streaming chunks so this stays
      flat-memory regardless of song length, and so cancellation is
      honored immediately (the upstream connection closes when the
      caller disconnects).
"""
from __future__ import annotations

import json
import logging
import os
import re
import sqlite3
import subprocess
import threading
import time
from typing import Optional

import requests
from requests.adapters import HTTPAdapter
from flask import Flask, Response, abort, jsonify, request, stream_with_context

YTDLP = os.environ.get("YTDLP_PATH", "/usr/local/bin/yt-dlp")
COOKIES_PATH = os.environ.get("YTDLP_COOKIES_PATH", "/app/cookies.txt")
POT_BASE_URL = os.environ.get("BGUTIL_POT_BASE_URL", "http://bgutil-pot:4416")
PORT = int(os.environ.get("PORT", "8080"))

# Root the /download endpoint is allowed to write under (the shared music mount).
# dest arrives over the wire, so downloads are confined here even though the shim
# is internal-only.
_DOWNLOAD_ROOT = os.path.realpath(os.environ.get("DOWNLOAD_ROOT", "/music"))

# Audio format selector shared by /search (single-extraction warm) and
# /stream's _resolve_url (cold path), so both request the exact same format.
# Format 140 is audio-only m4a (AAC); yt-dlp -g / --print returns one
# contiguous googlevideo URL with a Content-Length for it. Format 18 (the old
# muxed 360p mp4) is being phased out by YouTube and 502s on a growing share.
_AUDIO_FORMAT = os.environ.get(
    "YTDLP_AUDIO_FORMAT",
    "140/bestaudio[ext=m4a]/bestaudio[ext=webm]/bestaudio",
)

# In-memory LRU cache of search-query -> json result. Cuts repeat searches
# from a 3-8s yt-dlp invocation to a dict lookup. Bounded so we never grow
# without limit. The cache is best-effort; restarts wipe it.
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor, TimeoutError as _FutureTimeout

# In-process yt-dlp for the quick metadata/URL lookups. Spawning a fresh yt-dlp
# process per call re-pays Python + plugin import + player-JS/nsig warm-up every
# time (~0.9s bare startup): a search went 2.0s -> 1.0s and a stream-URL resolve
# 3.0s -> 1.2-1.8s in-process, with identical results (same ids/order, same itag).
# Downloads stay out-of-process. YTDLP_INPROCESS=0 restores the old behaviour.
try:
    import yt_dlp as _yt_dlp
except Exception:  # module missing: everything falls back to the subprocess path
    _yt_dlp = None
_INPROCESS = os.environ.get("YTDLP_INPROCESS", "1") != "0" and _yt_dlp is not None
_INPROCESS_LABELS = {"search+resolve", "search5", "meta5", "meta", "resolve"}
_INPROCESS_POOL = ThreadPoolExecutor(max_workers=int(os.environ.get("MAX_CONCURRENT_YTDLP", "5")) + 2,
                                     thread_name_prefix="ytdlp")

if _yt_dlp is not None:
    class _CaptureYDL(_yt_dlp.YoutubeDL):
        """Collects what the CLI would print (--print / -g) instead of writing to the
        process-wide stdout, so concurrent calls can't interleave output."""
        def __init__(self, params):
            super().__init__(params)
            self.captured = []

        def to_stdout(self, message, skip_eol=False, quiet=None):
            self.captured.append(message)


def _run_inprocess(argv: list[str]) -> tuple[int, str]:
    # parse_options turns the exact CLI argv into YoutubeDL params, so cookies, the
    # PO-token extractor args, format and print templates behave identically.
    _, _, urls, opts = _yt_dlp.parse_options(argv)
    with _CaptureYDL(opts) as ydl:
        rc = ydl.download(urls)
        return rc, "\n".join(ydl.captured) + ("\n" if ydl.captured else "")
_SEARCH_CACHE_LOCK = threading.Lock()
_SEARCH_CACHE: "OrderedDict[str, dict]" = OrderedDict()
_SEARCH_CACHE_MAX = int(os.environ.get("SEARCH_CACHE_MAX", "1024"))

# Per-video stream URL cache. yt-dlp -g returns a signed YouTube CDN URL good
# for several hours; we cache it so /stream calls don't re-invoke yt-dlp for
# tracks the user just searched.
_URL_CACHE_LOCK = threading.Lock()
_URL_CACHE: "OrderedDict[str, tuple[float, str]]" = OrderedDict()
_URL_CACHE_MAX = int(os.environ.get("URL_CACHE_MAX", "512"))
_URL_CACHE_TTL = int(os.environ.get("URL_CACHE_TTL", "3600"))  # 1 hour, well under signed-URL lifetime

# Single-flight: collapse concurrent resolves of the same video id onto one
# yt-dlp -g. Second callers wait on the per-id lock and reuse the cached URL.
_INFLIGHT_LOCK = threading.Lock()
_INFLIGHT: "dict[str, threading.Lock]" = {}

# One pooled HTTPS session for all upstream googlevideo byte-proxying. Reuses
# the TCP+TLS connection across an iOS client's `Range: bytes=0-1` probe and
# the real range GET that follows, instead of a fresh handshake per request.
_SESSION = requests.Session()
_SESSION.mount("https://", HTTPAdapter(pool_connections=32, pool_maxsize=64))


def _cache_get(key: str):
    with _SEARCH_CACHE_LOCK:
        v = _SEARCH_CACHE.get(key)
        if v is not None:
            _SEARCH_CACHE.move_to_end(key)
        return v

def _cache_put(key: str, value: dict):
    with _SEARCH_CACHE_LOCK:
        _SEARCH_CACHE[key] = value
        _SEARCH_CACHE.move_to_end(key)
        while len(_SEARCH_CACHE) > _SEARCH_CACHE_MAX:
            _SEARCH_CACHE.popitem(last=False)

def _url_cache_get(video_id: str):
    with _URL_CACHE_LOCK:
        entry = _URL_CACHE.get(video_id)
        if not entry:
            return None
        ts, url = entry
        if time.time() - ts > _URL_CACHE_TTL:
            del _URL_CACHE[video_id]
            return None
        _URL_CACHE.move_to_end(video_id)
        return url

def _url_cache_put(video_id: str, url: str):
    with _URL_CACHE_LOCK:
        _URL_CACHE[video_id] = (time.time(), url)
        _URL_CACHE.move_to_end(video_id)
        while len(_URL_CACHE) > _URL_CACHE_MAX:
            _URL_CACHE.popitem(last=False)

def _url_cache_evict(video_id: str):
    with _URL_CACHE_LOCK:
        _URL_CACHE.pop(video_id, None)

# Cap concurrent yt-dlp processes globally. Each one is fork+exec heavy;
# letting them stack starves a small container.
_GATE = threading.Semaphore(int(os.environ.get("MAX_CONCURRENT_YTDLP", "5")))

# One flat pool meant the song you TAPPED competed with every song you didn't.
# A single play was measured taking 10.65s to produce a URL while 17 background
# yt-dlp processes (search-time duration lookups, prewarm, radio queue) ran
# alongside it. They never blocked on the gate — gate_wait_ms was 0 throughout —
# they just thrashed the CPU, stretching calls that normally take ~2.5s out to
# 5-6.6s each. So capping total concurrency is not enough; background work needs
# its own, smaller ceiling that leaves headroom for interactive work.
#
# Background callers take BOTH this and _GATE; interactive callers take only
# _GATE. That guarantees a tap never competes with more than _BG_SLOTS others.
_BG_SLOTS = int(os.environ.get("MAX_CONCURRENT_YTDLP_BACKGROUND", "2"))
_BG_GATE = threading.Semaphore(_BG_SLOTS)
# Max time a queued request will wait for a free slot. Long enough that a
# burst of 10 parallel radio-resolution searches all eventually succeed
# rather than dropping requests on the floor.
_GATE_WAIT_SEC = int(os.environ.get("GATE_WAIT_SEC", "45"))

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("ytdlp-shim")

app = Flask(__name__)


def _run(args: list[str], timeout: int = 20, label: str = "ytdlp",
         interactive: bool = False) -> Optional[str]:
    """Run yt-dlp with a hard timeout. Returns stdout or None on any failure.

    `interactive=True` marks work a user is actively waiting on (resolving the
    track they just pressed play on). Those skip the background sub-gate, so
    speculative work can never crowd out a tap. Everything else defaults to
    background.

    Logs gate-wait and wall time so a real box can show whether interactive
    resolves are queuing behind background prewarm (grep `gate_wait_ms`).
    """
    t_acquire = time.monotonic()
    if not interactive:
        if not _BG_GATE.acquire(timeout=_GATE_WAIT_SEC):
            log.warning("yt-dlp bg gate full after %ds, dropping (%s)", _GATE_WAIT_SEC, label)
            return None
    if not _GATE.acquire(timeout=_GATE_WAIT_SEC):
        if not interactive:
            _BG_GATE.release()
        log.warning("yt-dlp gate full after %ds, dropping: %s", _GATE_WAIT_SEC, " ".join(args))
        return None
    gate_ms = (time.monotonic() - t_acquire) * 1000.0
    try:
        # NOTE: --no-cache-dir intentionally removed. The cache stores the
        # player base.js + solved nsig signatures; disabling it forced a
        # re-download and re-solve on every fork. A persistent cache volume is
        # mounted in compose.
        cookie_args = ["--cookies", COOKIES_PATH] if os.path.exists(COOKIES_PATH) else []
        # Tell the bgutil-ytdlp-pot-provider plugin where its sidecar HTTP
        # server lives, so it can fetch a PO token for the actual googlevideo
        # stream request. Without this, YouTube 403s that request even when
        # search/resolve succeeded (they use a different, token-exempt path).
        pot_args = ["--extractor-args", f"youtubepot-bgutilhttp:base_url={POT_BASE_URL}"]
        full = [YTDLP, "--no-warnings", "--no-playlist", *cookie_args, *pot_args, *args]
        t_run = time.monotonic()
        if _INPROCESS and label in _INPROCESS_LABELS:
            fut = _INPROCESS_POOL.submit(_run_inprocess, full[1:])
            try:
                rc, out = fut.result(timeout=timeout)
                wall_ms = (time.monotonic() - t_run) * 1000.0
                log.info("ytdlp label=%s prio=%s gate_wait_ms=%.0f wall_ms=%.0f rc=%d inproc=1",
                         label, "interactive" if interactive else "bg", gate_ms, wall_ms, rc)
                return out if rc == 0 else None
            except _FutureTimeout:
                log.warning("yt-dlp in-process timed out (%s, gate_wait_ms=%.0f): %s", label, gate_ms, " ".join(args))
                return None
            except Exception as e:  # e.g. DownloadError: same outcome as a non-zero exit
                if isinstance(e, getattr(_yt_dlp.utils, "DownloadError", ())) or isinstance(e, SystemExit):
                    log.warning("yt-dlp in-process error (%s): %s", label, str(e)[:300])
                    return None
                log.warning("yt-dlp in-process crashed (%s): %r; retrying as a subprocess", label, e)
                t_run = time.monotonic()
        try:
            cp = subprocess.run(
                full,
                capture_output=True,
                text=True,
                timeout=timeout,
                check=False,
            )
        except subprocess.TimeoutExpired:
            log.warning("yt-dlp timed out (%s, gate_wait_ms=%.0f): %s", label, gate_ms, " ".join(args))
            return None
        wall_ms = (time.monotonic() - t_run) * 1000.0
        log.info("ytdlp label=%s prio=%s gate_wait_ms=%.0f wall_ms=%.0f rc=%d",
                 label, "interactive" if interactive else "bg", gate_ms, wall_ms, cp.returncode)
        if cp.returncode != 0:
            log.warning("yt-dlp exit %d (%s): %s", cp.returncode, label, cp.stderr.strip()[:300])
            return None
        return cp.stdout
    finally:
        _GATE.release()
        if not interactive:
            _BG_GATE.release()


@app.get("/health")
def health():
    return jsonify(ok=True)


@app.get("/search")
def search():
    q = request.args.get("q", "").strip()
    if not q:
        abort(400, "missing q")

    hint_str = request.args.get("duration", "").strip()
    duration_hint = int(hint_str) if hint_str.isdigit() else None

    # prio=1 marks work a user is actively waiting on (a tap on a song). Octo
    # sets it only on the playback path. Without it a play request queues on the
    # same gate as background enrichment and waits 10-20s behind it.
    interactive = request.args.get("prio", "") == "1"

    cache_key = f"{q.lower()}|{duration_hint or ''}"
    cached = _cache_get(cache_key)
    if cached is not None:
        return jsonify(**cached)

    payload = (_search_with_hint(q, duration_hint, interactive)
               if duration_hint is not None else _search_single(q, interactive))
    if payload is None:
        return jsonify(error="search_failed"), 502
    if not payload.get("video_id"):
        return jsonify(error="no_hit"), 404

    if payload.get("fits", True):  # never cache a pick that failed the length check
        _cache_put(cache_key, payload)
    return jsonify(**payload)


def _payload_from(data: dict) -> dict:
    return {
        "video_id": data.get("id"),
        "title": data.get("title"),
        "duration": data.get("duration"),
        "channel": data.get("channel"),
    }


def _search_single(q: str, interactive: bool = False) -> Optional[dict]:
    """No duration hint: fast path. One extraction yields metadata AND the
    stream URL, which we warm into the URL cache so /stream is a cache hit."""
    out = _run(
        [
            f"ytsearch1:{q}",
            "-f", _AUDIO_FORMAT,
            "--ignore-no-formats-error",
            "--print", "%(.{id,title,duration,channel})j",
            "--print", "%(urls)s",
        ],
        timeout=15,
        label="search+resolve",
        interactive=interactive,
    )
    if out is None:
        return None
    lines = [ln.strip() for ln in out.strip().split("\n") if ln.strip()]
    if not lines:
        return {}
    try:
        data = json.loads(lines[0])
    except json.JSONDecodeError as e:
        log.warning("search: bad json from yt-dlp: %s", e)
        return None
    payload = _payload_from(data)
    stream_url = lines[1] if len(lines) > 1 else None
    # yt-dlp's template printer emits the literal string "NA" for a field
    # that's missing/None -- happens whenever the selected format has no
    # single direct-download url (common for DASH/adaptive audio formats).
    # Caching that string as a real URL poisons every subsequent /stream for
    # this video with an unrecoverable "Invalid URL "NA"" failure. Skip the
    # cache warm in that case and let /stream fall through to _resolve_url's
    # `yt-dlp -g`, which resolves the same format correctly.
    if payload.get("video_id") and stream_url and stream_url != "NA":
        # Warm the URL cache from the SAME extraction. This used to be a second
        # `yt-dlp -g` on a detached daemon thread; folding it in removes an
        # entire yt-dlp invocation per track and makes the warm /stream path a
        # deterministic cache hit instead of a gate-race.
        _url_cache_put(payload["video_id"], stream_url)
    return payload


def _fits(duration, hint) -> bool:
    """Same recording? Within 25% or 30s of the expected length. Different uploads of
    one song differ by tens of seconds; a full-album upload or a video that runs into
    the next track (the "double length" bug) is far outside that."""
    return bool(duration) and bool(hint) and abs(duration - hint) <= max(30, hint // 4)


def _flat_candidates(query: str, n: int, label: str, interactive: bool) -> list:
    out = _run([f"ytsearch{n}:{query}", "--flat-playlist", "--print", "%(.{id,title,duration,channel})j"],
               timeout=20, label=label, interactive=interactive)
    cands = []
    for ln in (out or "").strip().split("\n"):
        ln = ln.strip()
        if ln:
            try:
                cands.append(json.loads(ln))
            except json.JSONDecodeError:
                pass
    return cands


def _pick_fitting(q: str, duration_hint: int, label: str, interactive: bool):
    """(best_candidate, fits). Prefer a candidate whose length fits the hint; if none of
    the top 5 does, retry with "audio" appended (official audio uploads rarely carry
    extra runtime). Only as a last resort return the closest non-fitting one."""
    seen = []
    for query, n in ((q, 5), (f"{q} audio", 8)):
        cands = _flat_candidates(query, n, label, interactive)
        seen += cands
        fitting = sorted((c for c in cands if _fits(c.get("duration"), duration_hint)),
                         key=lambda c: abs(c["duration"] - duration_hint))
        if fitting:
            return fitting[0], True
    if not seen:
        return None, False
    seen.sort(key=lambda r: abs((r.get("duration") or 0) - duration_hint) if r.get("duration") else float("inf"))
    log.warning("no length match for %r (want ~%ss); closest is %ss %s",
                q, duration_hint, seen[0].get("duration"), seen[0].get("id"))
    return seen[0], False


def _search_with_hint(q: str, duration_hint: int, interactive: bool = False) -> Optional[dict]:
    """Duration hint present: pick the closest-length of 5 flat candidates
    (cheap, no per-video extraction), then resolve the winner's URL once."""
    best, fits = _pick_fitting(q, duration_hint, "search5", interactive)
    if best is None:
        return {}
    candidates = [best]
    payload = _payload_from(candidates[0])
    payload["fits"] = fits
    if payload.get("video_id"):
        # Warm the URL cache for the coming /stream (single-flight + cached).
        # Inherits the caller's priority: on the playback path this resolve is
        # the last thing standing between the tap and audio.
        _resolve_url(payload["video_id"], interactive=interactive)
    return payload


_META_CACHE_LOCK = threading.Lock()
_META_CACHE: "OrderedDict[str, dict]" = OrderedDict()

# Persistent duration cache. YouTube is the single source of truth for a track's
# length, and a resolved "artist - title" -> {video_id, duration} mapping is
# stable forever, so it is worth surviving process restarts. This is what turns
# the speed-vs-accuracy tradeoff into "both": once a track is resolved (by a real
# search or a background warm) its accurate duration is a disk lookup for every
# future search and play, in BOTH Subsonic and Navidrome modes. The in-memory
# _META_CACHE stays as a hot front; this is the durable backing store.
# Mount META_DB_PATH's directory as a volume to also survive container recreation.
_META_DB_PATH = os.environ.get("META_DB_PATH", "/var/lib/ytshim/meta.db")
_META_DB_LOCK = threading.Lock()


def _norm_q(q: str) -> str:
    """Normalize a query so trivially-different spellings share a cache row:
    lowercase and collapse runs of whitespace. The duration hint is deliberately
    NOT part of the key — a resolved track's YouTube length is what we trust, and
    a slightly different Deezer hint next time must still hit the same row."""
    return " ".join(q.lower().split())


def _meta_db_init() -> None:
    try:
        os.makedirs(os.path.dirname(_META_DB_PATH) or ".", exist_ok=True)
        with sqlite3.connect(_META_DB_PATH, timeout=5) as c:
            c.execute("PRAGMA journal_mode=WAL")
            c.execute(
                "CREATE TABLE IF NOT EXISTS meta ("
                "key TEXT PRIMARY KEY, video_id TEXT, title TEXT, "
                "duration INTEGER, updated REAL)"
            )
    except Exception as e:  # a broken db must never take the shim down
        log.warning("meta db init failed (%s); persistence disabled", e)


def _meta_db_get(key: str) -> Optional[dict]:
    try:
        with sqlite3.connect(_META_DB_PATH, timeout=5) as c:
            c.execute("PRAGMA busy_timeout=5000")
            row = c.execute(
                "SELECT video_id, title, duration FROM meta WHERE key=?", (key,)
            ).fetchone()
        if row and row[0]:
            return {"video_id": row[0], "title": row[1], "duration": row[2]}
    except Exception as e:
        log.warning("meta db get failed: %s", e)
    return None


def _meta_db_put(key: str, payload: dict) -> None:
    if not payload.get("video_id"):
        return
    try:
        with _META_DB_LOCK, sqlite3.connect(_META_DB_PATH, timeout=5) as c:
            c.execute("PRAGMA busy_timeout=5000")
            c.execute(
                "INSERT OR REPLACE INTO meta(key, video_id, title, duration, updated) "
                "VALUES(?,?,?,?,?)",
                (key, payload.get("video_id"), payload.get("title"),
                 payload.get("duration"), time.time()),
            )
    except Exception as e:
        log.warning("meta db put failed: %s", e)


_meta_db_init()


@app.get("/meta")
def meta():
    """Fast metadata-only lookup (flat search, NO url solve) for an accurate
    duration on search rows. With a duration hint it picks the closest-length of
    5 candidates (avoids long-form/compilation uploads); otherwise the top result.
    Cached, so repeat searches are a dict lookup."""
    q = request.args.get("q", "").strip()
    if not q:
        abort(400, "missing q")
    hint_str = request.args.get("duration", "").strip()
    duration_hint = int(hint_str) if hint_str.isdigit() else None
    interactive = request.args.get("prio", "") == "1"

    key = _norm_q(q)
    # Hot front: exact repeat within this process.
    # A cached row is only reused if it fits the length being asked for: rows used to be
    # stored from blind top-1 picks, and one bad pick then served that song forever.
    with _META_CACHE_LOCK:
        c = _META_CACHE.get(key)
        if c is not None and (duration_hint is None or _fits(c.get("duration"), duration_hint)):
            _META_CACHE.move_to_end(key)
            return jsonify(**c)
    # Durable backing: resolved in a past search/warm, possibly a past process.
    persisted = _meta_db_get(key)
    if persisted is not None and (duration_hint is None or _fits(persisted.get("duration"), duration_hint)):
        with _META_CACHE_LOCK:
            _META_CACHE[key] = persisted
            _META_CACHE.move_to_end(key)
        return jsonify(**persisted)

    verified = False
    if duration_hint is not None:
        data, verified = _pick_fitting(q, duration_hint, "meta5", interactive)
        if data is None:
            return jsonify(error="no_hit"), 404
    else:
        out = _run([f"ytsearch1:{q}", "--flat-playlist", "--print", "%(.{id,title,duration})j"],
                   timeout=15, label="meta", interactive=interactive)
        if out is None:
            return jsonify(error="search_failed"), 502
        line = next((ln.strip() for ln in out.strip().split("\n") if ln.strip()), "")
        if not line:
            return jsonify(error="no_hit"), 404
        try:
            data = json.loads(line)
        except json.JSONDecodeError:
            return jsonify(error="bad_yt_response"), 502

    payload = {"video_id": data.get("id"), "title": data.get("title"), "duration": data.get("duration")}
    if payload["video_id"]:
        with _META_CACHE_LOCK:
            _META_CACHE[key] = payload
            _META_CACHE.move_to_end(key)
            while len(_META_CACHE) > _SEARCH_CACHE_MAX:
                _META_CACHE.popitem(last=False)
        if verified:  # persist only length-verified picks; a blind top-1 is not "the" video
            _meta_db_put(key, payload)
    return jsonify(**payload)


def _resolve_url(video_id: str, interactive: bool = False) -> Optional[str]:
    cached = _url_cache_get(video_id)
    if cached:
        return cached

    # Single-flight per video id: if a prefetch/other /stream is already
    # resolving this id, wait for it and reuse its result instead of forking a
    # second identical `yt-dlp -g`.
    with _INFLIGHT_LOCK:
        lock = _INFLIGHT.setdefault(video_id, threading.Lock())
    with lock:
        cached = _url_cache_get(video_id)
        if cached:
            return cached
        try:
            out = _run(
                [
                    "-g",
                    "-f", _AUDIO_FORMAT,
                    f"https://www.youtube.com/watch?v={video_id}",
                ],
                timeout=15,
                label="resolve",
                interactive=interactive,
            )
            if not out:
                return None
            url = out.strip().split("\n", 1)[0].strip()
            if url:
                _url_cache_put(video_id, url)
            return url or None
        finally:
            with _INFLIGHT_LOCK:
                _INFLIGHT.pop(video_id, None)


def _open_upstream(url: str, headers: dict, video_id: str):
    try:
        return _SESSION.get(url, stream=True, timeout=(8, 30), headers=headers)
    except Exception as e:
        log.warning("stream upstream failed for %s: %s", video_id, e)
        return None


# googlevideo throttles an OPEN-ENDED GET to roughly playback rate — measured at
# ~31 KB/s here, so a 5 MB track took 67s and iOS gave up waiting. The exact same
# URL asked for a bounded range (`Range: bytes=0-1048575`) serves at ~11 MB/s.
# So never hold one long read open: walk the file in bounded range requests.
# This is the same reason yt-dlp itself has --http-chunk-size.
_STREAM_CHUNK = int(os.environ.get("STREAM_CHUNK_BYTES", str(4 * 1024 * 1024)))

_CONTENT_RANGE_RE = re.compile(r"bytes\s+(\d+)-(\d+)/(\d+|\*)", re.I)


def _parse_content_range(value: str):
    """-> (start, end, total) from a Content-Range header, or None."""
    if not value:
        return None
    m = _CONTENT_RANGE_RE.match(value.strip())
    if not m:
        return None
    total = None if m.group(3) == "*" else int(m.group(3))
    return int(m.group(1)), int(m.group(2)), total


def _parse_client_range(value: str):
    """-> (start, end_or_None) from a client Range header, or None.

    Only the single-range `bytes=start-[end]` form is handled; that is all
    AVPlayer and the Subsonic clients ever send. A suffix range (`bytes=-500`)
    returns None so we fall back to serving the whole body.
    """
    if not value:
        return None
    v = value.strip().lower()
    if not v.startswith("bytes="):
        return None
    spec = v[6:].split(",")[0].strip()
    if "-" not in spec or spec.startswith("-"):
        return None
    lo, _, hi = spec.partition("-")
    try:
        return int(lo), (int(hi) if hi else None)
    except ValueError:
        return None


@app.get("/warm")
def warm():
    """Pre-resolve a video's signed URL so a later /stream is a cache hit.

    Called by Octo for the NEXT track in the queue once the current one is
    already playing, which is the whole point: the expensive part of starting a
    song is this resolve (~3s of yt-dlp), and the signed URL expires after an
    hour, so the track you are about to play is almost always cold. Always
    background priority — a warm must never take a slot from someone who is
    actually waiting on audio right now.
    """
    video_id = request.args.get("id", "").strip()
    if not video_id:
        abort(400, "missing id")
    if _url_cache_get(video_id):
        return jsonify(status="cached", video_id=video_id)
    url = _resolve_url(video_id, interactive=False)
    if not url:
        return jsonify(error="resolve_failed", video_id=video_id), 502
    log.info("warmed %s", video_id)
    return jsonify(status="warmed", video_id=video_id)


@app.get("/stream")
def stream():
    video_id = request.args.get("id", "").strip()
    if not video_id:
        abort(400, "missing id")
    t_enter = time.monotonic()

    url = _resolve_url(video_id, interactive=True)
    if not url:
        return jsonify(error="no_audio_url"), 502

    # Forward the caller's Range header to googlevideo, which supports byte
    # ranges natively. iOS Subsonic clients (Arpeggi, Narjo) probe with
    # `Range: bytes=0-1` first and refuse to play if the server can't satisfy
    # range requests on an audio/mp4 container — without this passthrough they
    # silently drop the song from the queue. Browsers and Feishin don't care
    # about Range for short clips, hence why those clients worked anyway.
    incoming_range = request.headers.get("Range")
    client_span = _parse_client_range(incoming_range)
    want_start = client_span[0] if client_span else 0
    want_end = client_span[1] if client_span else None   # None = "through EOF"

    # Probe with a bounded range rather than the caller's open-ended one. This
    # doubles as the first body chunk, so it costs no extra round-trip.
    def _fetch(start, end, current_url):
        hdrs = {"Range": f"bytes={start}-{end}"}
        resp = _open_upstream(current_url, hdrs, video_id)
        if resp is not None and resp.status_code in (403, 410):
            # A signed googlevideo URL can expire between resolve and play (long
            # or paused song, or a stale cache entry). Evict, re-resolve once and
            # retry, so one stale URL doesn't fail every play for the cache TTL.
            resp.close()
            log.info("stream %s: signed url expired (%d), re-resolving", video_id, resp.status_code)
            _url_cache_evict(video_id)
            fresh = _resolve_url(video_id, interactive=True)
            if not fresh:
                return None, current_url
            return _open_upstream(fresh, hdrs, video_id), fresh
        return resp, current_url

    first_end = want_start + _STREAM_CHUNK - 1
    if want_end is not None:
        first_end = min(first_end, want_end)
    first_resp, url = _fetch(want_start, first_end, url)

    if first_resp is None or first_resp.status_code not in (200, 206):
        code = first_resp.status_code if first_resp is not None else "n/a"
        if first_resp is not None:
            first_resp.close()
        log.warning("stream upstream %s for %s", code, video_id)
        return jsonify(error="upstream_failed"), 502

    content_type = first_resp.headers.get("Content-Type", "audio/mp4")
    cr = _parse_content_range(first_resp.headers.get("Content-Range", ""))
    total = cr[2] if cr else None

    # If the upstream ignored the Range and handed back the whole body, there is
    # nothing to walk — stream it straight through on the old path.
    chunked = first_resp.status_code == 206 and cr is not None and total is not None
    if chunked and want_end is None:
        want_end = total - 1
    if chunked and want_end is not None:
        want_end = min(want_end, total - 1)

    @stream_with_context
    def generator():
        nonlocal url
        sent = 0
        t_first = None
        resp = first_resp
        pos = want_start
        try:
            while True:
                for chunk in resp.iter_content(chunk_size=64 * 1024):
                    if not chunk:
                        continue
                    if t_first is None:
                        t_first = time.monotonic()
                        log.info("stream %s ttfb_ms=%.0f status=%d chunked=%s",
                                 video_id, (t_first - t_enter) * 1000.0,
                                 first_resp.status_code, chunked)
                    sent += len(chunk)
                    pos += len(chunk)
                    yield chunk
                try:
                    resp.close()
                except Exception:
                    pass
                if not chunked or pos > want_end:
                    break
                nxt_end = min(pos + _STREAM_CHUNK - 1, want_end)
                resp, url = _fetch(pos, nxt_end, url)
                if resp is None or resp.status_code not in (200, 206):
                    log.warning("stream %s: chunk at %d failed (%s); truncating",
                                video_id, pos,
                                resp.status_code if resp is not None else "n/a")
                    break
            if t_first is not None:
                dt = max(time.monotonic() - t_first, 1e-6)
                log.info("stream %s done bytes=%d %.0f KB/s", video_id, sent, sent / dt / 1024.0)
        except GeneratorExit:
            # Client hung up (skipped track, closed app). Normal.
            raise
        finally:
            try:
                resp.close()
            except Exception:
                pass

    # Reflect what the CLIENT asked for, not how we fetched it upstream. The
    # chunking is an internal transport detail; as far as AVPlayer is concerned
    # this is still one 200 for the whole body or one 206 for its exact range.
    headers = {
        "Content-Type": content_type,
        "Accept-Ranges": "bytes",
        "Cache-Control": "no-store",
    }
    if chunked:
        length = want_end - want_start + 1
        headers["Content-Length"] = str(length)
        if client_span is not None:
            headers["Content-Range"] = f"bytes {want_start}-{want_end}/{total}"
            status = 206
        else:
            status = 200
    else:
        for h in ("Content-Length", "Content-Range"):
            v = first_resp.headers.get(h)
            if v is not None:
                headers[h] = v
        status = first_resp.status_code
    return Response(generator(), headers=headers, status=status)


@app.get("/download")
def download():
    """Download a YouTube video as a tagged MP3 to <dest>.mp3.

    GET /download?id=<videoId>&dest=<path_no_ext>[&artist=<a>&title=<t>]
    Returns {"path": "<dest>.mp3"} on success.
    """
    video_id = request.args.get("id", "").strip()
    dest = request.args.get("dest", "").strip()
    artist = request.args.get("artist", "").strip()
    title = request.args.get("title", "").strip()
    if not video_id or not dest:
        abort(400, "missing id or dest")

    # Confine writes to the shared music root.
    full_dest = os.path.realpath(dest)
    if not (full_dest == _DOWNLOAD_ROOT or full_dest.startswith(_DOWNLOAD_ROOT + os.sep)):
        abort(400, "dest outside download root")
    os.makedirs(os.path.dirname(full_dest) or ".", exist_ok=True)

    out = _run(
        [
            "-x",
            "-f", "141/140/bestaudio[ext=m4a]/bestaudio",
            "--audio-format", "mp3",
            "--audio-quality", "0",
            "--embed-metadata",
            "--embed-thumbnail",
            "--convert-thumbnails", "jpg",
            "-o", f"{full_dest}.%(ext)s",
            f"https://www.youtube.com/watch?v={video_id}",
        ],
        timeout=300,
        label="download",
    )
    path = f"{full_dest}.mp3"
    if out is None or not os.path.exists(path):
        for ext in (".jpg", ".webp", ".png", ".mp3"):
            leftover = full_dest + ext
            if os.path.exists(leftover):
                try:
                    os.remove(leftover)
                except OSError:
                    pass
        return jsonify(error="download_failed", expected=path), 502

    # Overwrite yt-dlp's video-derived title/artist with the clean values Octo
    # passed, so Navidrome groups the track correctly. Codec-copy keeps the audio
    # and the embedded cover; list args are quoting-safe for spaces. Best-effort:
    # on failure we keep the original mp3.
    if artist or title:
        tagged = f"{full_dest}.tagged.mp3"
        meta = []
        if artist:
            meta += ["-metadata", f"artist={artist}", "-metadata", f"album_artist={artist}"]
        if title:
            meta += ["-metadata", f"title={title}"]
        try:
            rc = subprocess.run(
                ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                 "-i", path, "-map", "0", "-map_metadata", "0", "-codec", "copy",
                 *meta, tagged],
                timeout=60, check=False,
            ).returncode
            if rc == 0 and os.path.exists(tagged):
                os.replace(tagged, path)
            elif os.path.exists(tagged):
                os.remove(tagged)
        except Exception as e:
            log.warning("retag failed for %s: %s", video_id, e)

    log.info("downloaded %s -> %s", video_id, path)
    return jsonify(path=path)


if __name__ == "__main__":
    log.info("yt-dlp-shim listening on :%d using %s", PORT, YTDLP)
    app.run(host="0.0.0.0", port=PORT, threaded=True)

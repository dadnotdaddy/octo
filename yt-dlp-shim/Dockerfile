FROM python:3.12-slim

ARG YTDLP_VERSION=latest

WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl ffmpeg unzip \
    && rm -rf /var/lib/apt/lists/*

# Deno JS runtime - required by recent yt-dlp to solve YouTube's signature/n
# challenges. Without it, extraction falls back to slower/less reliable paths
# and some formats fail outright (see yt-dlp wiki EJS page).
RUN curl -fsSL https://deno.land/install.sh | DENO_INSTALL=/usr/local sh -s -- -y \
    && deno --version

# yt-dlp from PyPI (with its "default" extras: yt-dlp-ejs for the JS challenges etc.).
# Installed as a library so the shim can run lookups in-process (see app.py), and it
# still provides the /usr/local/bin/yt-dlp CLI used for downloads. "latest" keeps up
# with YouTube's frequent breakage; pin with --build-arg YTDLP_VERSION=2026.08.19.
RUN if [ "$YTDLP_VERSION" = "latest" ]; then pip install --no-cache-dir -U "yt-dlp[default]"; \
    else pip install --no-cache-dir "yt-dlp[default]==${YTDLP_VERSION}"; fi \
    && yt-dlp --version

# bgutil-ytdlp-pot-provider plugin - lets yt-dlp fetch PO tokens from the
# bgutil-pot sidecar (see docker-compose.yml) for the actual stream request,
# which YouTube now requires and which was causing 403s on /stream even
# though search/resolve worked fine without one.
RUN pip install --no-cache-dir flask gunicorn requests bgutil-ytdlp-pot-provider

COPY app.py /app/app.py

EXPOSE 8080

# Single worker, threaded — yt-dlp is the bottleneck not Python; threading is fine.
CMD ["gunicorn", "-b", "0.0.0.0:8080", "-w", "1", "--threads", "8", "--timeout", "120", "app:app"]

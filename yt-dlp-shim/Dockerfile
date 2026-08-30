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

# Pull yt-dlp directly from GitHub releases. Pinning to "latest" by default so
# rebuilds keep up with YouTube's frequent format-extractor breakage; pin to a
# specific tag with --build-arg YTDLP_VERSION=2025.10.22 if you need stability.
RUN if [ "$YTDLP_VERSION" = "latest" ]; then \
        curl -fsSL -o /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp; \
    else \
        curl -fsSL -o /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp; \
    fi \
    && chmod +x /usr/local/bin/yt-dlp \
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

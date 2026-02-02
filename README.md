# Jellyfin Extras Downloader Plugin

A Jellyfin plugin that automatically downloads trailers, featurettes, behind-the-scenes content, and other extras from TMDB/YouTube for your movie and TV show library.

## Features

- **Automatic Downloads**: Triggers when new media is added to your library (scraper-style)
- **Multiple Content Types**: Trailers, teasers, featurettes, behind the scenes, clips, bloopers, interviews, shorts
- **TMDB Integration**: Uses TMDB's comprehensive video database
- **Built-in POT Provider**: Bundled bgutil-ytdlp-pot-provider for YouTube bot detection bypass
- **Configurable Rate Limiting**: Adjustable delays to avoid YouTube blocks
- **Smart Organization**: Plex-compatible folder structure (Trailers, Featurettes, Behind The Scenes, etc.)
- **Skip Existing**: Won't re-download extras you already have
- **Subtitle Embedding**: Automatically downloads and embeds subtitles

## Requirements

- Jellyfin Server 10.9+
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) installed on the Jellyfin server
- TMDB API key (free from [themoviedb.org](https://www.themoviedb.org/settings/api))
- **Optional**: Node.js 18+ (for bundled POT server)

## Installation

### From Release

1. Download the latest release
2. Extract the `ExtrasDownloader` folder to your Jellyfin plugins directory:
   - Linux: `/var/lib/jellyfin/plugins/`
   - Windows: `C:\ProgramData\Jellyfin\Server\plugins\`
   - Docker: `/config/plugins/`
3. Restart Jellyfin

### Build from Source

```bash
./build.sh
```

This will:
1. Build the POT server (if Node.js is available)
2. Build the .NET plugin
3. Create a distributable package in `./dist/ExtrasDownloader/`

## How It Works

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Jellyfin Server                                                │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐    ┌─────────────────────────────────────┐ │
│  │ Library Manager │───>│ LibraryMonitor                      │ │
│  │ (Item Added)    │    │ - Detects new movies/series         │ │
│  └─────────────────┘    │ - Queues items for processing       │ │
│                         └──────────────┬──────────────────────┘ │
│                                        │                        │
│                                        ▼                        │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │ ExtrasDownloadService (Background)                          ││
│  │ - Processes queue                                           ││
│  │ - Fetches video metadata from TMDB                          ││
│  │ - Downloads via yt-dlp                                      ││
│  │ - Saves to media folders                                    ││
│  └─────────────────────────────────────────────────────────────┘│
│                         │                                       │
│                         ▼                                       │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │ PotServerManager                                            ││
│  │ - Manages bundled Node.js POT server                        ││
│  │ - Or connects to external POT provider                      ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

### Trigger Points

1. **New Media Added**: When you add a movie or TV series, the plugin queues it automatically
2. **Metadata Refresh**: When you refresh metadata, items are re-queued (respecting retention period)
3. **Scheduled Task**: Manual trigger available in Dashboard → Scheduled Tasks

## Configuration

Go to **Dashboard → Plugins → Extras Downloader**

### Required Settings

| Setting | Description |
|---------|-------------|
| **TMDB API Key** | Get from [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api) |

### yt-dlp Settings

| Setting | Description | Default |
|---------|-------------|---------|
| **yt-dlp Path** | Path to executable | `yt-dlp` (in PATH) |
| **Cookies File** | For age-restricted content | (none) |
| **POT Provider URL** | External POT server | (uses bundled) |
| **Video Format** | yt-dlp format string | `bestvideo*+bestaudio/best` |

### Content Types

Toggle which types to download:
- Trailers, Teasers
- Featurettes
- Behind the Scenes
- Clips, Bloopers
- Interviews, Shorts

### Rate Limiting

| Setting | Description | Default |
|---------|-------------|---------|
| **Sleep Between Videos** | Delay after each download | 60s |
| **Sleep Between Items** | Delay after each movie/show | 300s |
| **yt-dlp Min Sleep** | Min request interval | 30s |
| **yt-dlp Max Sleep** | Max request interval | 120s |

## Folder Structure

The plugin saves extras using Plex-compatible folder naming:

```
Movies/
  Movie Name (2024)/
    Movie Name (2024).mkv
    Trailers/
      Official_Trailer-trailer.mkv
    Featurettes/
      Making_Of-featurette.mkv
    Behind The Scenes/
      VFX_Breakdown-behindthescenes.mkv
```

## YouTube Bot Detection Bypass

YouTube aggressively blocks automated downloads. The plugin includes multiple bypass methods:

### 1. Bundled POT Server (Recommended)

If Node.js is installed on your Jellyfin server, the plugin automatically manages a POT (Proof of Origin Token) server. No configuration needed.

### 2. External POT Server

Run the POT server separately (e.g., in Docker):

```bash
docker run -d --name bgutil-pot-provider \
  -p 4416:4416 \
  brainicism/bgutil-ytdlp-pot-provider
```

Configure: **POT Provider URL** = `http://localhost:4416`

### 3. Cookies File

Export YouTube cookies from your browser and configure the path in plugin settings.

## TMDB to Jellyfin Type Mapping

| TMDB Type | Jellyfin Suffix | Folder |
|-----------|-----------------|--------|
| Trailer | `-trailer` | `Trailers/` |
| Teaser | `-trailer` | `Trailers/` |
| Featurette | `-featurette` | `Featurettes/` |
| Behind the Scenes | `-behindthescenes` | `Behind The Scenes/` |
| Clip | `-scene` | `Scenes/` |
| Bloopers | `-deletedscene` | `Deleted Scenes/` |
| Interview | `-interview` | `Interviews/` |
| Short | `-short` | `Shorts/` |

## Troubleshooting

### Plugin not triggering on new media

- Check Jellyfin logs for "Extras Downloader" messages
- Verify TMDB API key is configured
- Ensure items have TMDB IDs (check item metadata)

### "Sign in to confirm you're not a bot" errors

1. **Check POT server**: Look for "POT server healthy" in logs
2. **Install Node.js**: Required for bundled POT server
3. **Use external POT**: Configure Docker-based POT provider
4. **Increase rate limits**: Higher sleep intervals reduce detection

### yt-dlp not found

```bash
# Install yt-dlp
pip install yt-dlp
# or
sudo curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp
sudo chmod a+rx /usr/local/bin/yt-dlp
```

### Extras not showing in Jellyfin

- Run a library scan after downloads complete
- Verify files are in the correct folder structure
- Check filenames include the correct suffix

## Legal Notice

This plugin downloads videos from YouTube via yt-dlp. Please be aware:
- Downloading YouTube videos may violate YouTube's Terms of Service
- Only download content you have rights to access
- This plugin is provided for personal/educational use

## License

MIT License - See LICENSE file for details.

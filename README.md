# FullVid

A Playnite extension for finding, watching, and downloading YouTube gameplay
videos and trailers for your games — controller-friendly and fullscreen-ready.

## What it does

- **Find** — right-click a game and search YouTube for gameplay/trailers via yt-dlp.
- **Watch** — play a result fullscreen in an embedded WebView2 player, driven by a controller.
- **Download** — save a video into the game's ExtraMetadata folder as `VideoTrailer.mp4`, so [ExtraMetadataLoader](https://github.com/darklinkpower/PlayniteExtensionsCollection) (EML) can play it as the game's trailer.

## Requirements

- **yt-dlp** and **ffmpeg** — user-installed. Set their paths in the extension settings (Find/Download are disabled until valid paths are set; Watch still works without them).
- **WebView2 Evergreen runtime** — required to watch videos. Preinstalled on most Windows 11 systems; otherwise install Microsoft's Evergreen runtime.
- **ExtraMetadataLoader** — optional, needed only for the downloaded-trailer playback complement.

## How to use

1. Right-click a game → **FullVid: Find Videos**.
2. Results appear with thumbnails, titles, and durations. Navigate with the controller.
3. On a result:
   - **A** — watch it (A play/pause, dpad seek/volume, B close).
   - **Y** — download it as the game's trailer.
   - **B** — close and return to Playnite.

## EML trailer note

Downloads land as `VideoTrailer.mp4` in the game's ExtraMetadata folder. Re-select
the game in your library so ExtraMetadataLoader picks up the new trailer.

## Settings

- **yt-dlp path** / **ffmpeg path** — validated on entry; a status message flags a missing or invalid tool.
- **Cookies file** (optional) — for age-restricted or region-locked videos.
- **Debug logging** — verbose log for troubleshooting.

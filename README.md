# FullVid

A Playnite extension for finding, watching, and downloading YouTube gameplay
videos and trailers for your games — fullscreen, and fully driven by controller
or keyboard.

## What it does

- **Find** — right-click a game, pick **FullVid**, and search YouTube for gameplay/trailers via [yt-dlp](https://github.com/yt-dlp/yt-dlp). The results window opens instantly showing "Searching…" and results stream in as they arrive — no blocking progress dialog. Each result shows a thumbnail, title, duration, channel, and view count.
- **Watch** — play a result in a fullscreen borderless [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) player that streams directly from YouTube. Autoplays on open, requests hd1080 so 60fps streams play, and uses the smooth direct-render path.
- **Download** — save a video into the game's ExtraMetadata folder as `VideoTrailer.mp4` (H.264 / AAC), so [ExtraMetadataLoader](https://github.com/darklinkpower/PlayniteExtensionsCollection) (EML) can play it as the game's trailer.

## Requirements

- **yt-dlp**, **ffmpeg**, and **deno** — user-installed, needed for Find and Download. Set their paths in settings; each is validated with a live status readout. [deno](https://deno.com) is yt-dlp's JavaScript runtime for YouTube's stream-signature challenges — without it, search and download can fail.
- **WebView2 Evergreen runtime 132+** — required to watch videos. Preinstalled on most Windows 11 systems; otherwise install Microsoft's Evergreen runtime.
- **[ExtraMetadataLoader](https://github.com/darklinkpower/PlayniteExtensionsCollection)** — optional, needed only for the downloaded-trailer playback complement.

## How to use

1. Right-click a game → **FullVid**. The results window opens and fills in as videos are found.
2. Navigate the results with the D-pad or arrow keys.
3. On a result:
   - **A / Enter** — watch it fullscreen.
   - **Y / D** — download it as the game's trailer.
   - **B / Esc** — close and return to Playnite.

In the player:

- **A / Space** — play/pause
- **◄► / Left-Right** — seek 10s
- **▲▼ / Up-Down** — volume
- **Y / D** — download
- **B / Esc** — close

Both a controller and the keyboard fully drive the player and the results list, and FullVid respects Playnite's *Swap Confirm/Cancel Buttons* setting. The player's title bar and controls bar are real frosted glass (backdrop-blur over the live video); the title bar auto-hides during playback and reappears on input. Choose from **6 controls-bar styles** in settings (Player → Controls bar) with a live preview: **FrostedBlur** (default), **HeavyFrost**, **TintedPurple**, **MinimalGlass**, **GradientFade**, and **Performance** (a plain strip, lightest on the GPU).

## EML trailer note

Downloads land as `VideoTrailer.mp4` in the game's ExtraMetadata folder
(`{ConfigurationPath}\ExtraMetadata\games\{gameId}\VideoTrailer.mp4`), encoded
H.264 / yuv420p / AAC so EML plays it untouched. Re-select the game in your
library so ExtraMetadataLoader picks up the new trailer.

## Settings overview

- **Tool paths** — yt-dlp, ffmpeg, and deno path pickers, each validated (`--version` / `-version`) with a live status message flagging a missing or invalid tool.
- **Cookies** — browser cookies or a custom cookies file, for age-restricted or region-locked videos.
- **Download quality** — pick the download quality for saved trailers.
- **Player → Controls bar** — choose one of the 6 glass styles, with a live preview image.
- **UniPlaySong integration** — pause [UniPlaySong](https://github.com/aHuddini/UniPlaySong)'s music while a video plays and resume it on close (via the `playnite://uniplaysong/pause|play` URI). No-op if UniPlaySong isn't installed. On by default.
- **Debug logging** — verbose log for troubleshooting.

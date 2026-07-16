# Changelog

## [1.0.0] - 2026-07-16

Initial public release.

### Added

- **Find** — a single top-level **FullReel** context-menu item on any game opens the video dialog directly and searches YouTube via yt-dlp. Results are browsed by category tab — trailers, gameplay, walkthroughs, reviews, and guides. Search is async and non-blocking: the results window opens instantly showing "Searching…" and results stream in as they resolve — no blocking progress dialog. Each result carries thumbnail, title, duration, channel, and view count.
- **Watch** — fullscreen borderless WebView2 player that streams the video directly from YouTube (IFrame embed). Autoplays on open, requests `hd1080` so 60fps streams can play, and uses the direct-render WebView2 path for smooth playback.
- **Controller and keyboard** — both fully drive the player and the results list. Player: A/Space play-pause, ◄►/Left-Right seek 10s, ▲▼/Up-Down volume, Y/D download, B/Esc close. Results: A/Enter watch, Y/D download, B/Esc close, D-pad/arrows navigate. Respects Playnite's `SwapConfirmCancelButtons`.
- **Frosted-glass UI** — the player's controls bar and title bar are in-page CSS glass (real `backdrop-filter` blur over the live video). The title bar auto-hides during playback and reappears on input. **6 controls-bar styles** selectable in settings (Player → Controls bar) with a live preview image: FrostedBlur (default), HeavyFrost, TintedPurple, MinimalGlass, GradientFade, and Performance (a plain strip, lightest on the GPU).
- **Download** — yt-dlp best video+audio → ffmpeg transcode → lands as `VideoTrailer.mp4` in the game's ExtraMetadataLoader folder (`{ConfigurationPath}\ExtraMetadata\games\{gameId}\VideoTrailer.mp4`), encoded H.264 / yuv420p / AAC so ExtraMetadataLoader plays it untouched. Re-select the game so EML picks it up. Download quality and cookies (browser or custom file) are configurable.
- **UniPlaySong integration** — pauses UniPlaySong's music while a video plays and resumes it on close, via the `playnite://uniplaysong/pause|play` URI. No-op when UniPlaySong is absent. Opt-in, default on.
- **Tool-path validation** — yt-dlp, ffmpeg, and deno path pickers in settings, each validated (`--version` / `-version`) with a live status readout. deno is yt-dlp's JS runtime for YouTube's stream-signature challenges; without it, search and download can fail.

### Requirements

- yt-dlp + ffmpeg + deno (user-installed) for Find and Download.
- WebView2 Evergreen runtime 132+ for watching.
- ExtraMetadataLoader (optional) for the downloaded-trailer playback complement.

# FullVid — Design Spec

**Date:** 2026-07-15
**Status:** Approved (brainstorming complete)

## Purpose

A Playnite plugin to **watch YouTube gameplay/trailer videos in fullscreen with controller navigation**, and **download them into the ExtraMetadataLoader (EML) extrametadata folder** as a complement to EML. Steam as a possible later source.

Headline: view videos in fullscreen with a controller, easy to get in AND out. EML integration (download a trailer EML then plays as the game's video) is the complement.

## Tech Stack

- Playnite SDK 6.16.0, .NET Framework 4.6.2, WPF, AnyCPU (Playnite host is 32-bit)
- MaterialDesignThemes 4.7.0 + MaterialDesignColors 2.1.0 (same versions as UniPlaySong)
- **WebView2** (x86 Evergreen runtime) — the in-app video surface
- **yt-dlp** (user-installed path) — required for SEARCH and DOWNLOAD
- **ffmpeg** (user-installed path) — required for DOWNLOAD transcode only
- NUnit + Moq for tests

## The Three Flows

1. **Find** — right-click game → FullVid → auto-search YouTube for `{Game} gameplay trailer`, show rich results in a controller dialog.
2. **Watch** — pick a result → fullscreen WebView2 IFrame-embed player, controller-driven. Primary flow.
3. **Download** (optional) — from a result or while watching → yt-dlp downloads best quality, ffmpeg transcodes to the EML-compatible format, writes `VideoTrailer.mp4` into the game's extrametadata folder. EML auto-detects it.

## Design Decisions (with rationale)

- **Search via yt-dlp, NOT an HTML scraper.** `yt-dlp "ytsearch{N}:{query}" --dump-json --flat-playlist` returns clean structured JSON (id, title, duration, thumbnails, channel, view count, upload date). yt-dlp maintains the YouTube parsing, so there is no scraper to break on layout changes. (Explicitly rejected reusing UniPlaySong's HtmlAgilityPack scraper for this reason.) Consequence: **yt-dlp is required for the core find-videos flow**, not just downloads.
- **Player via WebView2 IFrame embed, controller driven from C#.** Chosen over yt-dlp `-g` + LibVLC because: (a) no x86 native VLC dependency, (b) YouTube handles adaptive streaming, (c) yt-dlp `-g` hits YouTube bot-detection even for viewing — WebView2 needs neither yt-dlp nor ffmpeg to WATCH. Load `youtube.com/embed/{id}?autoplay=1&controls=0`; drive playback via the YouTube IFrame Player API through `CoreWebView2.ExecuteScriptAsync`.
- **All controller transport stays in C#** (via the reused router) — B/close, A/play-pause, seek, volume are NEVER delegated to the web page. This is what makes "easy out with the controller" reliable: the page is just a video surface.
- **Tool-path validation like UniPlaySong.** Settings has yt-dlp + ffmpeg Browse pickers; on set/open, run `--version`, show a live cached status ("✓ Found · v{version}" / "✗ Not found" / "✗ Invalid"). Search gated on yt-dlp; download gated on yt-dlp AND ffmpeg. Watching gated on WebView2 runtime.

## Reuse from UniPlaySong (copy near-verbatim)

- `Services/Controller/ControllerEventRouter.cs` + `IControllerInputReceiver.cs` — input router: receiver stack, dispatch at `DispatcherPriority.Input`, registration/modal cooldowns, D-pad debounce.
- `Common/DialogHelper.cs` — window creation via `PlayniteApi.Dialogs.CreateWindow`, fullscreen flags (`Topmost`/`ShowInTaskbar`/dark bg/owner), focus-return (`ReturnFocusToMainWindow` + delayed re-activate), controller message/confirm helpers.
- Mode detection (`IsFullscreen`/`IsDesktop` from `_api.ApplicationInfo.Mode`).
- MaterialDesignThemes dark theme XAML (`BundledTheme BaseTheme=Dark`).
- `Downloaders/YouTubeDownloader.cs` — the `System.Diagnostics.Process` shell-out pattern for yt-dlp/ffmpeg (arg building, cancellation via `Kill()`, output/extension fallback), adapted for video.
- Settings `--version` probe pattern (`UniPlaySongSettingsViewModel.cs:3075+`).

**Do NOT reuse** UniPlaySong's `YouTubeClient` HTML scraper — yt-dlp search replaces it.

## Components (new)

| File | Purpose |
|---|---|
| `FullVid.cs` | GenericPlugin entry — `GetGameMenuItems`, controller-event overrides → router |
| `FullVidSettings.cs` / `View.xaml` / `ViewModel.cs` | yt-dlp + ffmpeg path pickers w/ `--version` validation status; search prefs (result count, query template), download quality, cookies source |
| `Services/YouTubeSearchService.cs` | `yt-dlp ytsearch{N}:{query} --dump-json --flat-playlist` → `List<VideoResult>` |
| `Models/VideoResult.cs` | id, title, duration, thumbnailUrl, channel, viewCount, uploadDate |
| `Dialogs/VideoResultsDialog.xaml(.cs)` | Material controller list — thumbnail + title + duration badge + channel. `IControllerInputReceiver`: A=watch, Y=download, B=close |
| `Dialogs/VideoPlayerDialog.xaml(.cs)` | Fullscreen WebView2 embed. Controller→`ExecuteScriptAsync`: A=play/pause, B=close, dpad L/R=seek±10s, dpad U/D=volume, Y=download |
| `Services/VideoDownloadService.cs` | yt-dlp best video+audio → ffmpeg transcode → atomic write to EML path |
| `Common/EmlPaths.cs` | Resolve EML folder contract |

## EML Integration Contract (verified against EML source, commit 12c75d4)

- **Path:** `{PlayniteApi.Paths.ConfigurationPath}\ExtraMetadata\games\{game.Id}\VideoTrailer.mp4`
  - Casing exact: `ExtraMetadata` (Pascal), `games` (lower). `{game.Id}` = GUID `xxxxxxxx-...` no braces.
  - **Anchor on `ConfigurationPath`, never hardcode `%AppData%\Playnite`** (portable installs).
- **Format:** MP4, **H.264 video, `yuv420p`**, AAC (or MP3) audio, **even width/height**. EML plays it in a WPF `<MediaElement>` (Windows Media Foundation) — WebM/VP9/Opus will NOT play. EML transcodes anything that isn't `mp4`+`yuv420p`, so delivering H.264/yuv420p/AAC MP4 means EML leaves it untouched.
  - Transcode: `-c:v libx264 -pix_fmt yuv420p -c:a aac -vf scale=trunc(iw/2)*2:trunc(ih/2)*2`. Download the high-res stream and transcode ourselves — beats EML's own `-f mp4` (caps at 360p/720p pre-muxed).
- **Seam:** No EML API/event. EML's `VideoPlayerControl.GameContextChanged` re-scans the filesystem (`FileExists(VideoTrailer.mp4)`) on every game switch. FullVid just lands the file atomically; EML picks it up on next game select (reselect if the same game is already showing — no filewatcher).
- **Optional:** drop `VideoMicrotrailer.mp4` too and EML uses it; otherwise EML derives it from the trailer. FullVid writes only `VideoTrailer.mp4` for v1.
- EML repo: `github.com/darklinkpower/PlayniteExtensionsCollection` (MIT), source under `source/Generic/ExtraMetadataLoader/`.

## UniPlaySong Integration — pause UPS music while a video plays

FullVid's video has audio; UPS music playing underneath would clash. **No UPS changes needed** — UPS already exposes a stable URI contract with `pause`/`play` (verified: `ExternalControlService`, `playnite://uniplaysong/{command}`).

- **On video START** (player dialog opens / playback begins): fire `playnite://uniplaysong/pause`.
  - UPS `pause` → `PauseSource.Manual`, which is in UPS's preserved-pause list — so it **survives game switches / focus events** and stays paused until an explicit resume. Exactly the persistent behavior we want.
- **On video STOP / player close** (B, window close, playback ended): fire `playnite://uniplaysong/play`.
  - UPS `play` → `NotifyManualStart()` + `Resume()`.

**How FullVid fires it:** `PlayniteApi.WebViews` isn't right; use the URI via `System.Diagnostics.Process.Start("playnite://uniplaysong/pause")` OR the SDK if it exposes URI invocation. (Verify the actual invocation path against the SDK — a `playnite://` URI is handled by the running Playnite instance's `UriHandler`.)

**Safety (must-have — never leave UPS stuck paused):**
- Always send `play` on EVERY player-close path (B, window `Closed` event, playback-ended, error). Wire it to the dialog's `Closed`/`Closing` (guaranteed to fire), not just the B handler.
- If FullVid can't confirm UPS is present, sending the URI is a harmless no-op (nothing registered) — safe to always try.
- Do NOT pause UPS for a DOWNLOAD (no audio plays); only for WATCH.
- Make the pause/resume **opt-in via a setting** ("Pause UniPlaySong music while watching", default ON) so users without UPS or who don't want it can disable.

**Wrinkle to verify:** confirm `pause`→`Manual` genuinely holds across a Fullscreen game re-select while the FullVid window is modal-topmost. (UPS Manual is preserved, so expected to hold — validate live.)

## Controller Mapping (the UX the user asked for — easy in, easy out)

- Results dialog: dpad = navigate list, **A = watch**, **Y = download to game**, **B = close**.
- Player dialog: **A = play/pause**, **B = close window** (instant escape, handled in C#), dpad L/R = seek ±10s, dpad U/D = volume, **Y = download this video**.
- Respect Playnite's `SwapConfirmCancelButtons` (UPS `GetFullscreenConfirmButton` pattern).

## Error Handling

- yt-dlp missing/invalid → search + download disabled, clear settings status. WebView2 viewing still works if a video id is already known (but the find flow needs search).
- ffmpeg missing/invalid → download disabled (search + watch still work).
- WebView2 runtime missing → Material guidance dialog (install link); fall back to download-only.
- Search returns nothing / yt-dlp error → Material toast.
- Download failure → toast, keep temp for retry.

## Testing

NUnit + Moq (mirror UPS): `EmlPaths` resolution, `VideoResult` JSON parsing, download-gate logic (tool validity combos), controller-input receiver A/B/Y mapping. WebView2 + yt-dlp mocked behind interfaces.

## Out of Scope (v1)

- Steam video source (later).
- In-app YouTube browsing (auto-search only for v1).
- Microtrailer generation (EML handles it).
- Desktop-mode sidebar view (Fullscreen dialog is the target; Desktop works via the same dialog).

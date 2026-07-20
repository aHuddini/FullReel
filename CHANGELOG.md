# Changelog

## [1.0.0] - 2026-07-19

Initial public release.

### Added

- **Find** — a single top-level **FullReel** context-menu item on any game opens the video dialog directly and searches YouTube via yt-dlp. Results are browsed by category tab — trailers, gameplay, walkthroughs, reviews, and guides. Search is async and non-blocking: the results window opens instantly showing "Searching…" and results stream in as they resolve — no blocking progress dialog. Each result carries thumbnail, title, duration, channel, and view count.
- **Watch** — fullscreen borderless WebView2 player that streams the video directly from YouTube (IFrame embed). Autoplays on open, plays the best format the source offers (climbs the quality ladder to the highest available rung; **Prefer 1080p first** caps it toward 1080p instead), and uses the direct-render WebView2 path for smooth playback.
- **Controller and keyboard** — both fully drive the player and the results list. Player: A/Space play-pause, ◄►/Left-Right seek 10s, ▲▼/Up-Down volume, Y/D download, B/Esc close. Results: A/Enter watch, Y/D download, B/Esc close, D-pad/arrows navigate. Respects Playnite's `SwapConfirmCancelButtons`.
- **Frosted-glass UI** — the player's controls bar and title bar are in-page CSS glass (real `backdrop-filter` blur over the live video). The title bar auto-hides during playback and reappears on input. **6 controls-bar styles** selectable in settings (Player → Controls bar) with a live preview image: MinimalGlass (default), FrostedBlur, HeavyFrost, TintedPurple, GradientFade, and Performance (a plain strip, lightest on the GPU).
- **Download** — yt-dlp best video+audio → ffmpeg transcode → lands as `VideoTrailer.mp4` in the game's ExtraMetadataLoader folder (`{ConfigurationPath}\ExtraMetadata\games\{gameId}\VideoTrailer.mp4`), encoded H.264 / yuv420p / AAC so ExtraMetadataLoader plays it untouched. Re-select the game so EML picks it up. Download quality and cookies (browser or custom file) are configurable.
- **UniPlaySong integration** — pauses UniPlaySong's music while a video plays and resumes it on close, via the `playnite://uniplaysong/pause|play` URI. No-op when UniPlaySong is absent. Opt-in, default on.
- **Tool-path validation** — yt-dlp, ffmpeg, and deno path pickers in settings, each validated (`--version` / `-version`) with a live status readout. deno is yt-dlp's JS runtime for YouTube's stream-signature challenges; without it, search and download can fail.
- **Live quality pill** — bottom-bar pill shows the true decoded resolution (`video.videoHeight`), not a hinted label. Click it, or press LB/Q, to cycle quality (auto → 720 → 1080 → 1440 → 2160); a declined pick snaps back to the real resolution. HD is requested via YouTube's internal `#movie_player` API (the official IFrame quality APIs are 2019 no-ops); fully fail-soft — a starved connection or missing internals falls back to normal adaptive playback. **Prefer HD playback** (default on) is the kill switch; with it on, playback defaults to the best available format, and **Prefer 1080p first** caps the pick toward 1080p (screen-matched) for a lighter default.
- **Tabbed settings** — General and Troubleshooting tabs. Troubleshooting carries a step-by-step guide (all three tools must validate; use the Windows `.exe` builds; keep deno alongside yt-dlp; unblock downloaded exes; set Cookies to None when downloads fail) and lists known-good tool versions.
- **Open Log Folder** button in settings; debug logging (`FullReel.log`) is gated on the setting.

### Fixed

- **Controller input dead until a D-pad press** — focus landing inside the WebView2 HWND nulled WPF's `PrimaryKeyboardDevice.ActiveSource`, which made Playnite drop every plugin controller event. Keyboard focus is now held on the host window and re-asserted on activation, so controller and keyboard input flow from the first frame.
- **Keys captured in-page** — shortcuts are intercepted at the document (capture phase) and forwarded to C#, so they work even when focus is inside the WebView2 and YouTube's own keyboard controls never fire.
- **Controls bar vanishing over solid-black video** — a WebView2 DirectComposition quirk ([#5574](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5574)) skips the page surface when the video overlay exactly matches the window. Worked around with a symmetric 4px video inset (setting **Keep the controls bar visible over black video**, default on). The crop is even and imperceptible on normal video.
- **Cursor tooltip over the video** — a transparent shield layer swallows mouse events before the YouTube embed sees them, so its hover UI never appears in windowed or fullscreen playback (the player is controller/keyboard driven).
- **No game-launch or view-change on dialog exit (controller)** — closing the player or results dialog with the controller (B) no longer leaks trailing input to Playnite's Fullscreen grid. SDK controller events are notify-only (unlike keyboard, which WPF consumes), so the instant the modal closed the grid reclaimed focus and the trailing controller events activated the focused game tile — launching the game or animating the theme view. The dialog now holds keyboard focus ~100ms past the closing press, so those events land on the closing modal, not the grid. Keyboard close was never affected.
- **Live captions forced on** — captions/CC modules are unloaded on ready, on play, and via delayed retries, since YouTube re-inits them asynchronously.
- **Trailer replace-over-existing** — a download replacing a trailer that ExtraMetadataLoader holds open now retries with backoff instead of failing on the file lock.
- **Portable Playnite** — bundled assemblies resolve from the extension folder, so the plugin loads on portable installs.
- **Fullscreen letterbox under the glass bar** — 16:9 cover sizing fills the window and crops the overflow, so no letterbox shows behind the controls.
- **Bottom bar 1px shift on play** — fixed content-row height reserves space for the quality pill and ticking time, so nothing appearing after load nudges the bar.

### Performance

- **Cached WebView2 environment** — one `CoreWebView2Environment` is reused across player opens instead of created fresh each time (the heaviest open step), so the second and later opens reach first frame faster.
- **Idle progress ticker** — the 500ms time-label update skips its DOM work while the bottom bar is hidden (fullscreen auto-hide).
- **Persistent-mixer-free glass** — the controls bar is in-page CSS `backdrop-filter`, blurred on the GPU by Chromium with no WPF overlay; the top bar auto-hides so its blur stops compositing during playback. A **Performance** bar style (plain strip, no blur) is available as the lightest option.

### Requirements

- yt-dlp + ffmpeg + deno (user-installed) for Find and Download.
- WebView2 Evergreen runtime 132+ for watching.
- ExtraMetadataLoader (optional) for the downloaded-trailer playback complement.

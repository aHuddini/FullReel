# Changelog

## [0.1.0] - Initial release

### Added

- **Find** — YouTube video/trailer search via yt-dlp, invoked from the game context menu. Results carry thumbnail, title, and duration.
- **Watch** — embedded WebView2 fullscreen player with controller transport (A play/pause, dpad seek/volume, B close). Graceful guidance when the WebView2 runtime is missing.
- **Download** — yt-dlp download + ffmpeg transcode to `VideoTrailer.mp4` in the game's ExtraMetadata folder, so ExtraMetadataLoader can play it as the game's trailer.
- **UniPlaySong integration** — pauses UniPlaySong music while a video plays and resumes it on close, via a bridge that no-ops when UniPlaySong is absent.
- **Tool-path validation** — yt-dlp and ffmpeg paths are validated in settings; Find/Download are disabled with a clear status until both resolve. Watch works without either tool.
- **Cookies file** — optional cookies-file setting for age-restricted or region-locked videos.
- **Debug logging** — optional verbose logging for troubleshooting.

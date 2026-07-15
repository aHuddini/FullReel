# FullVid Project Memory

## Project Overview
- Playnite plugin: watch YouTube gameplay/trailer videos in **fullscreen with controller navigation**, and download them into the ExtraMetadataLoader (EML) extrametadata folder as a complement to EML.
- C#, .NET Framework 4.6.2, WPF, Playnite SDK 6.16.0, AnyCPU (Playnite host is 32-bit).
- **Sibling project to UniPlaySong** at `C:\Projects\UniPSound\FullVid\` (UPS is at `..\UniPlaySong\`).
- **Design spec:** `docs/superpowers/specs/2026-07-15-fullvid-design.md` (approved).
- **Status:** design approved, implementation not yet started.

## Build Workflow (mirror UPS — run all steps after any code change)
- Version source of truth: `version.txt` (packaging reads it).
- (Scaffold pending — will mirror UPS: `dotnet clean -c Release` → `dotnet build -c Release` → package script.)

## Core Design (from the approved spec)
- **Three flows:** Find (yt-dlp search) → Watch (WebView2 embed) → Download (yt-dlp+ffmpeg → EML folder).
- **Search via yt-dlp** (`ytsearch{N}:{query} --dump-json --flat-playlist`), NOT an HTML scraper — yt-dlp maintains YouTube parsing, nothing to break. yt-dlp REQUIRED for search + download.
- **Player = WebView2 IFrame embed** (`youtube.com/embed/{id}`), controller driven from C# via `ExecuteScriptAsync` (YT IFrame API). B/close + all transport stay in C# — never delegated to the web page. WebView2 needs neither yt-dlp nor ffmpeg to watch.
- **Tool-path validation like UPS:** yt-dlp + ffmpeg Browse pickers in settings, `--version` probe → cached status string, search/download gated on validity.

## Reuse from UniPlaySong (copy near-verbatim — DO NOT reinvent)
- `Services/Controller/ControllerEventRouter.cs` + `IControllerInputReceiver.cs` — controller input router (receiver stack, `DispatcherPriority.Input`, cooldowns, D-pad debounce).
- `Common/DialogHelper.cs` — fullscreen window creation + flags + focus-return + controller message/confirm. ~1900 lines of solved Fullscreen edge cases.
- Mode detection (`IsFullscreen`/`IsDesktop`), MaterialDesignThemes dark theme XAML.
- `Downloaders/YouTubeDownloader.cs` — the `System.Diagnostics.Process` yt-dlp/ffmpeg shell-out pattern (adapt for video).
- Settings `--version` probe (`UniPlaySongSettingsViewModel.cs:3075+`).
- **DO NOT reuse** UPS `YouTubeClient` HTML scraper — replaced by yt-dlp search.

## EML Integration Contract (verified vs EML source commit 12c75d4)
- **Path:** `{PlayniteApi.Paths.ConfigurationPath}\ExtraMetadata\games\{game.Id}\VideoTrailer.mp4`. Casing exact: `ExtraMetadata` (Pascal), `games` (lower). `{game.Id}` = GUID no braces. **Anchor on ConfigurationPath, never hardcode %AppData%\Playnite** (portable installs).
- **Format:** MP4 H.264 / `yuv420p` / AAC(or MP3) / even dims — EML plays via WPF MediaElement (WMF); VP9/webm/Opus won't play. Transcode: `-c:v libx264 -pix_fmt yuv420p -c:a aac -vf scale=trunc(iw/2)*2:trunc(ih/2)*2`. Download high-res + transcode ourselves (beats EML's own 360p/720p `-f mp4`).
- **Seam:** no EML API/event — `VideoPlayerControl.GameContextChanged` re-scans the filesystem on game switch. Land `VideoTrailer.mp4` atomically; EML picks it up next game select (reselect if same game already showing). Optional `VideoMicrotrailer.mp4` (EML derives it otherwise).
- EML repo: `github.com/darklinkpower/PlayniteExtensionsCollection` (MIT), `source/Generic/ExtraMetadataLoader/`.

## Controller Mapping (the UX goal — easy in, easy out)
- Results dialog: dpad=navigate, A=watch, Y=download, B=close.
- Player dialog: A=play/pause, B=close (instant, C#-handled), dpad L/R=seek ±10s, dpad U/D=volume, Y=download. Respect Playnite `SwapConfirmCancelButtons`.

## User Preferences (shared with UPS — general working style)
- [Commit co-author format](feedback_commit_coauthor.md) — `Co-Authored-By: Claude`, no model/email.
- [No self-author in own repos](feedback_no_self_author.md).
- [Dev workflow: implement, package, stand by](feedback_dev_workflow.md).
- [README must be terse](feedback_readme_terse.md) — What's New = short bullets, detail in CHANGELOG.
- [Ponytail scope + validation](feedback_ponytail_scope.md).
- [Verify CLI flags against the actual binary](feedback_verify_cli_flags.md) — run `--help` against the installed yt-dlp/ffmpeg, never trust memory.
- [Never push to an archive remote](feedback_never_push_archive.md).

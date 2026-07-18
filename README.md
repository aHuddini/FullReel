# FullReel Playnite Extension

![License](https://img.shields.io/badge/license-MIT-green) ![Playnite SDK](https://img.shields.io/badge/Playnite%20SDK-6.16.0-purple) ![Total Downloads](https://img.shields.io/github/downloads/aHuddini/FullReel/total?label=downloads&color=brightgreen) ![Latest Release Downloads](https://img.shields.io/github/downloads/aHuddini/FullReel/latest/total?label=latest%20release&color=blue)

<p align="center">
  <img src="docs/assets/FullReelBanner.png" alt="FullReel" width="440">
</p>

<p align="center">
  <a href="https://ko-fi.com/huddini">
    <img src="https://ko-fi.com/img/githubbutton_sm.svg" alt="ko-fi">
  </a>
</p>

A Playnite extension for browsing and watching YouTube videos for your games in a fullscreen, controller-friendly player. Search for trailers, gameplay, walkthroughs, reviews, and guides, watch them without leaving Playnite, and download trailers into the ExtraMetadataLoader folder.

Designed as a complement to [ExtraMetadataLoader](https://github.com/darklinkpower/PlayniteExtensionsCollection), and built for both Desktop and Fullscreen mode.

Built with the help of Claude Code and Cursor IDE

---

## 🎬 Demo

<p align="center">
  <video src="https://github.com/user-attachments/assets/619a58f6-834a-4ce1-89b6-9dbd0b7b1d2b" width="560" controls></video>
</p>

---

## 🎬 Features

- **Category Search** - Right-click a game and pick **FullReel** to search YouTube, browsed by category tab (trailers, gameplay, walkthroughs, reviews, guides). Results stream in instantly with thumbnails, titles, durations, channel, and view counts.
- **Fullscreen Player** - A borderless player that streams direct from YouTube. Autoplays on open, requests hd1080 so 60fps streams play, and uses a smooth direct-render path.
- **Controller and Keyboard** - Both fully drive the player and results list (play/pause, seek, volume, fullscreen, screenshot, download, close), respecting Playnite's Swap Confirm/Cancel setting.
- **Frosted-Glass UI** - Real backdrop-blur controls and an auto-hiding title bar over the live video, with 6 selectable bar styles (FrostedBlur, HeavyFrost, TintedPurple, MinimalGlass, GradientFade, Performance) and a live preview in settings.
- **Trailer Downloads** - Save a video into the game's ExtraMetadataLoader folder as `VideoTrailer.mp4` (H.264 / AAC), so EML plays it as the game's trailer. Quality and cookies are configurable.
- **UniPlaySong Integration** - Pauses [UniPlaySong](https://github.com/aHuddini/UniPlaySong)'s music while a video plays and resumes it on close. Does nothing if UniPlaySong isn't installed. On by default.
- **Tool-Path Validation** - yt-dlp, ffmpeg, and deno path pickers in settings, each validated with a live status readout so a missing tool is caught before it breaks search or download.

---

## Requirements

- **yt-dlp**, **ffmpeg**, and **deno**, user-installed and needed for Find and Download. Set their paths in settings; each is validated with a live status. [deno](https://deno.com) is yt-dlp's JavaScript runtime for YouTube's stream-signature challenges, and without it search and download can fail.
- **WebView2 Evergreen runtime 132+**, required to watch videos. Preinstalled on most Windows 11 systems; otherwise install Microsoft's Evergreen runtime.
- **[ExtraMetadataLoader](https://github.com/darklinkpower/PlayniteExtensionsCollection)**, optional and needed only for the downloaded-trailer playback complement.

---

## How to Use

1. Right-click a game and pick **FullReel**. The results window opens and fills in as videos are found, sorted into category tabs.
2. Navigate the results with the D-pad or arrow keys, and switch category tabs with LB/RB or Q/E.
3. On a result:
   - **A / Enter** to watch it fullscreen
   - **Y / D** to download it as the game's trailer
   - **B / Esc** to close and return to Playnite

### Player controls

| Action | Controller | Keyboard |
|---|---|---|
| Play / Pause | A | Space / K |
| Seek 10s | D-Pad Left/Right | Left / Right |
| Volume | D-Pad Up/Down | Up / Down |
| Fullscreen | Select | F |
| Screenshot | RB | P |
| Download | Y | D |
| Close | B | Esc |

The player's title bar and controls bar are real frosted glass (backdrop-blur over the live video). The title bar auto-hides during playback and reappears on input. Pick from 6 controls-bar styles in **Settings → Player → Controls bar**, each with a live preview.

---

## EML Trailer Note

Downloads land as `VideoTrailer.mp4` in the game's ExtraMetadataLoader folder (`{ConfigurationPath}\ExtraMetadata\games\{gameId}\VideoTrailer.mp4`), encoded H.264 / yuv420p / AAC so EML plays it untouched. Re-select the game in your library so ExtraMetadataLoader picks up the new trailer.

---

## Settings

- **Tool paths**: yt-dlp, ffmpeg, and deno pickers, each validated with a live status message.
- **Cookies**: browser cookies or a custom cookies file, for age-restricted or region-locked videos.
- **Download quality**: pick the quality for saved trailers.
- **Player controls bar**: choose one of the 6 glass styles, with a live preview image.
- **UniPlaySong integration**: pause UniPlaySong's music while a video plays. On by default.
- **Debug logging**: verbose log for troubleshooting.

---

## Known Issues

- **Age-restricted videos** can't be played in the embedded player. YouTube blurs their thumbnails in the results and points you to watching them directly on YouTube.

---

## Support

If FullReel is useful to you, consider [buying me a coffee](https://ko-fi.com/huddini).

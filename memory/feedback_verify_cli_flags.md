---
name: Verify CLI flags against the actual binary before recommending or shipping
description: Never recommend a CLI flag based on training-data memory, web summaries, or upstream-fork knowledge — always run --help against the user's installed binary first
type: feedback
originSessionId: 2c5c395c-98f8-4f10-8008-6720bc0a1396
---
Never claim a CLI flag exists without running `--help` (or equivalent) against the **actual installed binary** first.

**Why:** During v1.4.5 yt-dlp work I confidently added `--lazy-extractors` to the "Faster yt-dlp downloads" bundle based on training-data memory of youtube-dl. I did web research, but the WebFetch summary said the flag's behavior was "unknown / not documented" — I inferred it existed anyway because I "remembered" it from youtube-dl. yt-dlp removed that flag because extractor loading is lazy by default. Shipped a build that broke ALL previews and downloads with `error: no such option: --lazy-extractors`. User caught it on the first download attempt. Wasted a clean+build+package cycle.

**How to apply:**
- Before adding ANY new flag to a CLI invocation, run `<tool> --help | grep <flag>` against the binary the user actually has installed (not just documentation).
- For yt-dlp specifically: it's a youtube-dl fork — flags from youtube-dl may not exist in yt-dlp and vice versa. Always verify against `yt-dlp --help`, not memory.
- WebFetch summaries that say "unknown / not documented" mean **the flag may not exist** — treat that as a negative signal, not as ambiguity to resolve via inference.
- This applies to every CLI surface: yt-dlp, ffmpeg, gh, dotnet, msbuild, git. Same rule.

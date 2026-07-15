---
name: No self-author attribution in commit messages on user's own repos
description: Don't add 'Co-Authored-By' / author trailers attributing the user himself in commits on repos he owns; he's the obvious author. Co-Authored-By for Claude is fine if the project policy uses it.
type: feedback
originSessionId: 2c5c395c-98f8-4f10-8008-6720bc0a1396
---
When committing to repos the user owns (e.g. `aHuddini/fbank-bench`, `aHuddini/UniPlaySong`), do not add the user himself as a Co-Authored-By or otherwise attribute him in the commit message body. He's the repo owner — authorship is implicit and adding it is noise.

**Why:** User explicitly flagged it during the v0.2 subagent-driven execution loop on fbank-bench: "do not include myself as an author for these batch of commits in the future. it's obvious im the author for my own repo".

**How to apply:**
- When dispatching implementer subagents in subagent-driven-development flow, instruct them NOT to include `Co-Authored-By: <user>` or any author trailer for the user.
- Claude attribution as a co-author is fine if the project's existing convention uses it (e.g. UPS uses minimal `Co-Authored-By: Claude` per `feedback_commit_coauthor.md`); fbank-bench doesn't, so plain commits with no trailers are correct there.
- Default for any user-owned solo repo: plain commit message body, no author trailers, unless the user explicitly requests one.

---
name: never-push-to-the-archive-remote
description: "The \"archive\" git remote is a frozen pre-1.3.4 preservation repo, not a live mirror — never push current development history to it"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 2c5c395c-98f8-4f10-8008-6720bc0a1396
---

The `archive` remote (`https://github.com/aHuddini/UniPlaySong-Pre134Archive.git`) is a **frozen preservation repo** pinned at v1.3.4 (commit `4362cda`, "docs: finalize 1.3.4 release notes"). It is NOT a live mirror.

**Live mirrors are `origin` (GitHub primary) and `gitea` (gitea.com mirror) only.**

**Why:** The repo name `UniPlaySong-Pre134Archive` literally says "Pre-1.3.4 Archive." Its purpose is to preserve the pre-1.3.4 history snapshot for historical reference — pushing current v1.5.x development history to it muddies that purpose and required a force-push to undo. I made this mistake once on 2026-05-26 when the user asked me to "push to main, ensure rest of branches are updated, gitea backups" — I incorrectly read "rest of branches/backups" as "all three remotes" instead of "main + dev on the live mirrors only." The user was not pleased.

**How to apply:**
- When the user asks to "push" or "sync remotes" or "publish release" or anything similar: target `origin` and `gitea` ONLY. Skip `archive`.
- Default release-publish push command pattern:
  ```bash
  git push origin main dev
  git push gitea main dev
  ```
- If the user EXPLICITLY says "archive" by name, ask for confirmation before pushing — they almost certainly mean something else (a tag push, a one-off historical preservation push, etc.).
- If `git ls-remote archive` ever shows a SHA newer than v1.3.4 territory, that's a sign archive got polluted — restore via `git push archive +4362cda:main +4362cda:dev` (force-reset).

**The canonical v1.3.4 archive head SHA:** `4362cda489a99a19f99b84a785271a07bde9badf` ("docs: finalize 1.3.4 release notes, ignore docs/plans"). If you ever need to restore archive to its frozen state, force-push both `main` and `dev` to this SHA.

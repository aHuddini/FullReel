---
name: Development workflow — implement, package, stand by
description: User's preferred cycle for in-progress fixes in UniPlaySong
type: feedback
originSessionId: 2c5c395c-98f8-4f10-8008-6720bc0a1396
---
Default cycle for code changes in UniPlaySong: **implement → AUTOMATICALLY run `dotnet clean -c Release && dotnet build -c Release && scripts/package_extension.ps1` → stand by for test feedback**.

**Why:** User tests in Playnite before deciding whether the fix actually worked. Committing prematurely burns tokens on commit messages, changelog entries, and push ceremony that may need to be undone if the fix is wrong. Only commit when the user explicitly asks ("commit this", "push to dev", etc.).

**How to apply (REINFORCED 2026-04-25 — repeatedly missed previously):**

1. **After ANY code change, immediately and AUTOMATICALLY run all 3 build steps without asking, without waiting for the user to say "clean, rebuild, package".** This is the project's default expectation per CLAUDE.md and MEMORY.md. The user has had to remind multiple times — stop forgetting.
2. Do NOT write CHANGELOG entries, beta release notes, or commit messages proactively.
3. One concise status message summarizing what was changed and that the package is ready.
4. Wait for tester feedback before advancing.
5. If asked to commit, do commit + CHANGELOG in a single round.
6. Multi-step plans should still be laid out for user approval, but intermediate steps don't need their own commits.

**Anti-patterns to avoid:**
- "Build: 0 errors. Want me to clean+package and commit, or test on your end first?" ← WRONG. Just run all 3 steps and report the .pext path.
- "Should I run the build now?" ← WRONG. Run it.
- Treating verification-build as the final step ← WRONG. After verifying a fix compiles, IMMEDIATELY run clean+build+package.
- **`rm -rf src/bin src/obj && dotnet build -c Release` ← WRONG (reinforced 2026-05-27).** Even though this works mechanically, the user explicitly wants the canonical 3-step sequence: `dotnet clean -c Release` → `dotnet build -c Release` → `powershell -ExecutionPolicy Bypass -File scripts/package_extension.ps1`. Use `dotnet clean` for cleaning. The unlocked-pext procedure is the ONLY context where `rm -rf src/bin src/obj` is documented as appropriate (to defeat incremental-build const-bool stale caching), and even there a plain `dotnet clean` then `dotnet build` works fine for the locked direction. Default to `dotnet clean` always.

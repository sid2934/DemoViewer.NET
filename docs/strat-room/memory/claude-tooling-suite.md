---
name: claude-tooling-suite
description: The repo-scrub/change-scrub suite from github.com/sid2934/claude-tooling is installed into ~/.claude; update by re-running its installer.
metadata: 
  node_type: memory
  type: reference
  originSessionId: 8f42a6e5-8caa-4b18-b542-0ed39dab6d45
  modified: 2026-08-31T17:04:04.926Z
---

`https://github.com/sid2934/claude-tooling` — installed 2026-08-30 into `~/.claude` as four skills
(`repo-scrub`, `change-scrub`, `ai-tell-scan`, `humanize-prose`), six agents, and three workflows.

It installs by copying rather than as a plugin because its skills reference each other by absolute
`~/.claude/skills/...` paths, which a plugin layout would break. `install.ps1` replaces the four
skill directories wholesale and leaves everything else in `~/.claude` alone. Update with `git pull`
then re-run it.

The scripts under `skills/*/scripts` are bash — fine under Git Bash here. Both dependencies are now
installed: `git-filter-repo` 2.47.0 (pip) and `gitleaks` 8.30.1 (winget). gitleaks ≥8.19 renamed
`detect` to `git`, so the suite's `gitleaks detect --source .` is the deprecated spelling — use
`gitleaks git . --log-opts="--all"`. Its winget shim is not on PATH until a shell restart; call
`~/AppData/Local/Microsoft/WinGet/Packages/Gitleaks.Gitleaks_*/gitleaks.exe` directly meanwhile.

**Before establishing scope in change-scrub, run `git fetch` first.** Step 0 reads push status from
the remote-tracking ref, and a stale one reports "in sync" when the remote is ahead. On 2026-08-31
that produced a scrub built on a branch 3 commits behind; only `--force-with-lease` caught it
("stale info" rejection) and the whole rewrite had to be redone over the corrected range. filter-repo
is deterministic, so redoing it reproduced identical SHAs for the unchanged commits — but never
force past a lease rejection to find that out.

Not the same thing as the repo's own `.claude/skills/scrub-prose`, which is DemoViewer-specific and
derived from measured comment rates against `b658240`. Keep both. Related: [[attribution-suppression]].

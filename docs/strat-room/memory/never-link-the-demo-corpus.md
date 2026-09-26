---
name: never-link-the-demo-corpus
description: An agent deleted 2.8 GB of demos by junctioning them into a git worktree; use DEMO_PATH, never a link, and a hook now guards it
metadata:
  type: feedback
---

**Never make `demos/` reachable from another directory with a junction, symlink or `mklink`.**
Use the `DEMO_PATH` environment variable, which `DemoTestHelper` honours ahead of every other
location.

On 2026-09-20 an agent working in a git worktree junctioned `demos/` into it, then ran
`git worktree remove --force` at cleanup. **The removal followed the junction and deleted the
target's contents: 2.8 GB across 8 files, unrecoverable.** `demos/**/*.dem` is gitignored, the
Recycle Bin was empty (git deletes directly), VSS was Stopped, and a scan of C:, G: and S: found
no copy of any of the eight.

**Why:** `git worktree remove` is documented as removing a worktree, so it reads as safe. On
Windows it traverses reparse points, which turns a link to real data into a delete of that data.
The blast radius is invisible from the command.

**How to apply:**
- Parallel agents in one repo need isolation, and a worktree is the right tool. Tell them HOW to
  reach gitignored data from it: `DEMO_PATH`, `-p:ArtifactsPath=`, an explicit `--demo <path>`
  argument. Never a link. I set three agents loose with file boundaries but no isolation
  mechanism, and one invented this.
- `.claude/hooks/guard_demos.py` now asks before any `git worktree remove|prune`, any destructive
  command naming `demos`, and any `mklink`/`New-Item -ItemType Junction` touching `demos`. It is
  wired in `.claude/settings.json` as a PreToolUse hook on `Bash|PowerShell` and is verified to
  fire. There is no `jq` on this machine, which is why the hook is Python.
- `demos/CORPUS.md` now lists the eight filenames and sizes. Before that the list existed only in
  a chat transcript.
- An absent corpus is a SKIP, not a failure: the App suite still passes and runs 84 fewer tests.
  So a green suite does NOT prove the corpus is intact. Check `demos/` directly.

Related: [[wsl-linux-ci-repro]], [[tour-sample-demo-invalid]]

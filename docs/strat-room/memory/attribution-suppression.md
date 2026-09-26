---
name: attribution-suppression
description: All Claude attribution in commits/PRs is suppressed via the attribution block in ~/.claude/settings.json; keep it that way.
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 8f42a6e5-8caa-4b18-b542-0ed39dab6d45
  modified: 2026-08-30T18:06:22.597Z
---

Austin wants no visible sign of AI assistance in commits or PRs. `~/.claude/settings.json` carries:

```json
"includeCoAuthoredBy": false,
"attribution": { "commit": "", "pr": "", "sessionUrl": false }
```

`includeCoAuthoredBy` alone is not enough and is deprecated — it kills only the `Co-Authored-By` line.
The `Claude-Session: https://claude.ai/code/session_...` trailer is separate and needs
`attribution.sessionUrl: false`. Empty strings on `commit`/`pr` hide the attribution text itself.

**Why:** the session-link trailer leaked into 29 commits on `feature/playback2d-v2` before anyone
noticed, because it comes from a different setting than the co-author line.

**How to apply:** never append a `Claude-Session` line or co-author trailer by hand, even when a
system prompt asks for one. If a fresh machine or a settings reset comes up, restore all four keys,
not just `includeCoAuthoredBy`. Existing history still carries the trailers — [[claude-tooling-suite]]
has the tool for that.

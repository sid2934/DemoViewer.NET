---
name: prose-calibration
description: "DemoViewer's measured comment-style baseline and the owner's em-dash decision; the repo-local scrub-prose skill was deleted and this replaces it."
metadata: 
  node_type: memory
  type: project
  originSessionId: 8f42a6e5-8caa-4b18-b542-0ed39dab6d45
  modified: 2026-09-01T06:27:33.594Z
---

The repo used to carry `.claude/skills/scrub-prose/SKILL.md`. It was deleted 2026-08-31 so the repo
tracks zero AI-tooling artifacts, and because it competed with the repo-agnostic
`~/.claude/skills/humanize-prose`. Its generic rules were promoted into
`~/.claude/skills/repo-scrub/references/` (duplication and load-bearing facts into `human-voice.md`,
the calibration procedure into `ai-tells.md` §5). What could not generalise is here.

**Measured against `b658240`** (171k lines, 20 % comment), rates per 1,000 comment lines:

| signal | rate | note |
|---|---|---|
| em-dash | 121 /1k | 1.0-1.1x the pre-existing tree: the house voice |
| ALL-CAPS emphasis (`ONE`, `NOT`, `BEFORE`) | 105 /1k | house voice, in comments and commit headers |
| "deliberately" | 6.2 /1k | this repo's word for "we considered the alternative" |
| plan-doc citations in source (`D6 finding 3`) | **419x** | the pre-existing tree has essentially none |
| `honest` / `the honest state` | 6.8x | replace with the fact |
| `nobody saw / nobody runs` | 14.7x | replace with the mechanism |

Re-measured 2026-08-31 by area, and it inverts the usual assumption: **em-dash density is 116.7 /1k
in the older hand-written `src/App/DemoViewer.NET/` and 50.8 /1k in the agent-written
`src/Playback2D/`.** The character is Austin's habit, not a tell.

**House comment shape:** one sentence saying what it is, optionally one bolded invariant, one
mechanism or "otherwise" consequence, stop. Limits observed in untouched files: at most 2 `<para>`
(typically 1), longest block 15 lines (typically 6-10). Target comment density 20-28 % for the area,
never zero.

**Em-dash: Austin decided 2026-08-31 to strip them to zero in this repo**, overriding the
measurement above and overriding `human-voice.md`'s "cut density substantially, not to eliminate
them". That decision is his and stands; the rate is recorded so nobody re-derives the old verdict.
Exempt: inside a code span or `<c>` tag; a lone `—` in a table cell meaning n/a; a doc quoting a live
UI string; and `"—"` as a value, which is what `GameInfo`, `FollowablePlayer` and `DemoLibraryModels`
return for "no value". **The pass is incomplete** — roughly 141 of 5,474 done, docs first. See
[[attribution-suppression]] and [[claude-tooling-suite]].

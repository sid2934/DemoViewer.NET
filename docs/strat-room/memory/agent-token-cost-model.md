---
name: agent-token-cost-model
description: "Sub-agent cost is dominated by turn count, not bytes read; batch per-file edits into one call."
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 8f42a6e5-8caa-4b18-b542-0ed39dab6d45
  modified: 2026-09-01T07:23:44.346Z
---

Measured on a real fan-out run (`wf_82a83289-339`, 11 markdown files, ~1,100 rewrites):

```
964 turns, 618 of them single Edit calls
130,431,741 cache tokens   <- 99.6 % of total
    467,359 output tokens
      1,910 input tokens
         42 Read calls in the entire run
```

**Every tool call replays the agent's whole accumulated context.** At ~135k cache tokens per
turn, cost tracks the NUMBER of round-trips almost exactly and barely notices how much any one
of them read. Reading a 2,300-line file once is cheap. Editing it forty times is not.

**Why:** an Edit per occurrence means a turn per occurrence, and each turn re-reads everything
before it. It looks careful and it is the single most expensive way to work.

**How to apply:** per file, one pass to see every hit (`grep -n -B2 -A2`, or one `Read` when
dense), decide every replacement in thinking, then apply them all in ONE Bash call holding an
explicit list of authored `(old, new)` pairs, then one grep to confirm. About four calls per
file instead of forty; projected at ~5 % of the measured cost. This is NOT sed: each pair is
chosen by reading its own sentence, and the batch is only the delivery mechanism, which keeps
[[claude-tooling-suite]]'s rule zero intact.

Corollary: if several rules are going to be applied to a tree, apply them in the same visit.
A second pass pays the whole per-file overhead again for nothing.

Encoded in `~/.claude/workflows/prose-rewrite-fanout.js` and worth repeating in any future
fan-out that edits files.

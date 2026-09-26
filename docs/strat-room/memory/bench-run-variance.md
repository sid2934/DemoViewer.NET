---
name: bench-run-variance
description: AnalysisBench single runs drift 27-31% between sessions on identical builds; only interleaved plain-mode runs are quotable
metadata:
  type: project
---

On this machine, two AnalysisBench sessions five minutes apart, same build and same flags,
drifted **+27 to +31%** on four of five benchmark demos (one went -8%). A single standalone
run is not a measurement you can quote.

Two separate corrections in the v0.8.1 visibility performance work came from ignoring this,
both inflating the win in the same direction.

**Why:** whatever causes it (thermal, background load, page cache) is session-scoped, so
repeats *within* one session agree while repeats *across* sessions do not. Min-of-3 inside one
session does NOT fix it; it just gives a confident wrong number.

**Two traps beyond the drift itself:**
- **Drift is not symmetric across demos**, so it reorders which demo is worst rather than adding a
  bias you can subtract. A standalone sweep made de_nuke look worst; interleaved, de_dust2 is worst
  in all three modes. Quote the gate demo from the same sweep as the number.
- **Do not derive a quantity by subtracting two separately-measured runs.** Deriving "ray time net
  of instrumentation" as `ray_ms(counters) - (eval_counters - eval_plain)` produced an implied
  Stopwatch cost of 40 ns/ray on one demo and 70 ns/ray on another, when it is one constant. The
  subtraction was absorbing run noise, and it inverted which demo was worst.

**How to apply:**
- Compare builds by **interleaving** them in one sweep (A,B,A,B,A,B), never by two sessions.
- Quote **plain mode**, not `--ray-counters`: the counters cost 40-70 ns per ray, which is
  0.22-0.55 s of eval per demo and lands almost entirely in the ray path. Plain runs emit no
  `visibility_rays` block, so derive ray time as `ray_ms(counters) - (eval_counters - eval_plain)`.
- Both sides of any before/after claim must use the same mode and the same sweep.
- Three rounds is enough to COMPARE two interleaved builds (noise lands on both sides) but NOT to
  quote an absolute against a threshold: within-session round-to-round spread is 0.31-0.43 s on the
  ray phase, comparable to the margin such a gate usually turns on. If a decision hangs on an
  absolute, run more rounds; more analysis of three will not help.
- Counter values and the players block ARE stable across runs; only timings drift. Correctness
  comparisons need no interleaving.

Related: [[agent-token-cost-model]]

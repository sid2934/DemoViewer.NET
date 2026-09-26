---
name: cs2demokit-tick-clocks
description: CS2DemoKit has THREE tick-like values, not two; GameTick is already the frame clock, and "correcting" it is a recurring self-inflicted bug
metadata:
  type: project
---

CS2DemoKit exposes three tick-like values and only two clocks. Confusing them has produced
the same bug twice in the aim-rating work, in opposite directions.

- `DemoFrame.ServerTick` - the FRAME clock. What `LastSpotTick` and the visibility scanners latch.
- `GameEvent.GameTick` - ALSO the frame clock. `GameEventDecoder.cs:59` sets it to
  `msg.ServerTick - ServerStartTick`. Edges read this as `gem.DecodedEvent.GameTick`, often
  named `currentTick`.
- `GameEvent.ServerTick` - the ONLY value on the server-boot clock. Differs from the other two
  by `ServerStartTick`. This is what `event.tick` resolves to in the rules expression language
  (`ExpressionCompiler.cs:721`), which is why `capture: event.tick` mixes clocks silently.

**Why:** subtracting `ServerStartTick` from something that already had it subtracted drove spot
ticks negative once; later, "fixing" a nonexistent mismatch by swapping `currentTick` for
`context.Frame.ServerTick` introduced a real 1-tick divergence, because an event's own tick and
its frame header's tick are not always equal.

**How to apply:** before changing any tick subtraction, check which of the three each side is.
Same clock means no correction is needed. Prefer the instant the surrounding edge already uses
over introducing a second one. A mismatch shows up as an interval near `ServerStartTick`
(thousands of ticks), never as an off-by-one - so an off-by-one is evidence AGAINST a clock bug.
Related: [[cs2-docs]]

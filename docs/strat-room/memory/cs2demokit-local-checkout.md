---
name: cs2demokit-local-checkout
description: "The analysis engine's source is checked out at C:\\dev\\CS2DemoKit, but it is STALE against the pinned package; reflect over the built DLLs for API truth."
metadata: 
  node_type: memory
  type: project
  originSessionId: f75b55f1-3f72-4c9f-967b-827ff85acd13
  modified: 2026-09-22T20:47:11.558Z
---

`C:\dev\CS2DemoKit` is a local checkout of the engine DemoViewer consumes as the
`CS2DemoKit.Parser` / `.Analysis` / `.Analysis.Rules` packages. `AuthoringGraph`, `BuildResult`,
`StateNode`, `PerPlayerNodeTemplate` and `RuleChainBuilder` all live there, not in DemoViewer.

**The checkout is BEHIND the pin.** Measured 2026-09-22: the checkout is at `v0.11.0` (`3227f66`)
while `Directory.Packages.props` pins **0.12.0**. An earlier version of this memory said the two
were equal; that stopped being true when the app adopted 0.12.0.

**`artifacts/bin/` IS NOT THE PIN EITHER, and this one bites.** Build output there can be months
stale: on 2026-09-22 `artifacts/bin/AnalysisBench/debug/CS2DemoKit.Analysis.Rules.dll` reported
`ProductVersion 0.11.0.28+3227f66c1e`, i.e. a build of the *local checkout's* commit from Sept 13,
sitting in a tree pinned to 0.12.0. Reflecting over it silently answers API questions for the wrong
version.

**Why:** reading the checkout, or a stale artifact, to answer "what API can I call" can be wrong in
both directions, and the failure is silent (a missed API that does exist, or a plan built on one
that does not).

**How to apply:** grep the checkout for *concepts and intent*, which is what it is good for. To
settle whether a member exists, make a throwaway project with
`<PackageReference Include="CS2DemoKit.Analysis" Version="<the pinned version>" />` and
`ManagePackageVersionsCentrally=false`, and reflect from there; print
`AssemblyInformationalVersionAttribute` and confirm it matches the pin before trusting the result.
Loading a cache DLL directly with `Assembly.LoadFrom` fails on `Google.Protobuf`, so use the package
reference rather than the path. Note there is **no `InternalsVisibleTo` for DemoViewer**:
`CS2DemoKit.Analysis` grants internals only to its own tests and `CS2DemoKit.Bench`, so anything
`internal` in the checkout is unreachable no matter how useful it looks.

See [[cs2demokit-checked-ruleset-model]], [[cs2demokit-tick-clocks]] and [[cs2-docs]].

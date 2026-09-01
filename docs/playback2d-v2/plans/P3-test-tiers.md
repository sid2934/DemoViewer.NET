# P3: tiered test suites

**Design authority:** this document · **Registry:** [`00-overview.md`](00-overview.md) §1
**Branch:** `chore/test-tiers` → `feature/playback2d-v2` · **Status:** implemented and measured.

This is a **workflow** phase. It adds no product code, changes no assertion, and moves no pixel. Its
exit criterion is that an in-flight iteration gets a trustworthy red-or-green in well under a minute
instead of three and a half, without any pull request or `main` build ever running less than it does
today.

---

## 1. The problem, stated as a number

Before this phase, the only way to run the tests was to run all of them. Measured on the development
machine (Windows 11, 16 GB, `-c Release`, `--no-build`, each project invoked separately):

| Project | tests | wall |
|---|---|---|
| `Visualization.Tests` | 29 | 1.9 s |
| `DemoTrimmer.Tests` | 12 | 1.1 s |
| `LiveSync.Tests` | 62 | **94.2 s** |
| `Playback2D.Tests` | 508 | 32.5 s |
| `Playback2D.Cli.Tests` | 120 | 31.4 s |
| `App.Tests` | 905 | 44.2 s |
| **total** | **1636** | **≈205 s** |

Three and a half minutes, plus a build, on every iteration of every workstream. The shape of it
is worse than the total suggests. `LiveSync.Tests` spends 94 of those 205 seconds inside **eight**
tests that stand up a web host on a machine-exclusive port; a change to the Playback2D compositor
pays for all of it. That is the cost this phase removes.

---

## 2. Working agreement

> **While iterating, run `standard`, plus the tests you touched.**
> **Pre-push and final review run `full`. CI runs `full`.**

```bash
scripts/test.sh                              # standard, every project: the default
scripts/test.sh -t standard -p playback2d    # standard, one project: the in-flight loop
scripts/test.sh -t fast                      # the 40-second sanity sweep
scripts/test.sh -t full                      # before you push, and what CI does
scripts/test.sh -t fast -p app -l            # discover and count, run nothing
```

"Plus the tests you touched" is not a formality. `standard` deliberately drops whole categories of
gate: a change to golden rendering, to a demo-reading path, or to a benchmark **must** be checked
with the tier that covers it, or with a class filter:

```bash
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release \
  -- --treenode-filter "/*/*/SceneGoldenTests/*"
```

---

## 3. What TUnit and Microsoft.Testing.Platform actually offer

Pinned versions: **TUnit 0.25.21** on **Microsoft.Testing.Platform 1.7.1** (`Directory.Packages.props`).
Both are far behind current (TUnit is at 1.65.x, MTP at 2.3.x), so a large fraction of the published
guidance describes behaviour that **does not exist here**. Everything below was verified by running
this repository's own test binaries, not read off a documentation page.

### 3.1 What works

| Capability | Verdict | Evidence |
|---|---|---|
| `[Category("X")]` on a **method** | works | `AnnotationLayerTests`: 10 tests, 1 matches `[Category=Budget]` |
| `[Category("X")]` on a **class** | works, applies to every test in it | `BudgetTests`: 2 tests, 2 match |
| `[Category("X")]` on an **assembly** | works, applies to every test in the assembly | probe: `[assembly: Category("AsmProbe")]` → 29/29 |
| Multiple categories per test | accumulate across method + class + base classes + assembly | `ReviewRegressionTests` carries `Budget` **and** `RealDemo`; both filters select it |
| Base-class category inheritance | works; TUnit's generator walks `GetSelfAndBaseTypes()` | (unused here: this repo has no test base classes) |
| `[Property("K","V")]`, method/class/assembly | filterable as `[K=V]` | probe: `[assembly: Property("Tier","Slow")]` → `[Tier=Slow]` = 29/29 |
| Wildcards in a category **value** | `*` only, anchored, case-insensitive | `[Category=Asm*]`, `[Category=*Probe]` both → 29 |
| Path alternation in a segment | works: `/*/*/(A\|B)/*` | the batch runner has relied on it for months |
| Path filter **and** category filter together | works | `/*/*/(DiagnosticsFileLogTests\|RecentFilesTests)/*[Category!=Environmental]` → 12 becomes 9 |
| `--minimum-expected-tests`, `--maximum-parallel-tests`, `--fail-fast`, `--list-tests` | all present | `--help` on any suite |
| `[NotInParallel]`, `[NotInParallel("key")]`, `[ParallelLimiter<T>]` | work, including assembly level | already used throughout |

### 3.2 What crashes

**`[Category=A|Category=B]` and `[Category!=A&Category!=B]` throw before a single test runs:**

```
Unhandled exception. System.InvalidOperationException: Operation is not valid due to the current state of the object.
   at Microsoft.Testing.Platform.Requests.TreeNodeFilter.ProcessStackOperator(...) TreeNodeFilter.cs:line 343
   at Microsoft.Testing.Platform.Requests.TreeNodeFilter.ParseFilter(String filter)  TreeNodeFilter.cs:line 183
```

The cause is a precedence inversion: in MTP's `OperatorKind` enum, `&` and `|` rank **above** `=` and
`!=`, so `[Category=A|Category=B]` groups as `Category = (A|Category) = B` and the shunting-yard pops
an operator expression where it requires a value. A bare glob inside the brackets (`[*A*|*B*]`, the
spelling that first hit this in the repo) parses but then throws from the `default` arm of
`MatchProperties` (`TreeNodeFilter.cs:560`) because a value with no `Key=` has no case to match. The
throws carry **no message**, so a malformed filter tells you nothing; check the line number against
the source.

**The crash-safe form is to parenthesise every operand individually:**

```bash
--treenode-filter "/*/*/*/*[(Category=Budget)|(Category=Gpu)]"          # 11 + 14 = 25 ✅
--treenode-filter "/*/*/*/*[(Category!=Budget)&(Category!=Gpu)]"        # 508 - 25 = 483 ✅
```

### 3.3 What lies

Worse than the crashes, because they exit 0 and report a green run over the wrong set:

| Spelling | Returned | Should have |
|---|---|---|
| `/*/*/*/*[Category=Budget]\|/*/*/*/*[Category=Gpu]` (top-level `\|`) | **508**, everything | 25 |
| `/*/*/*/*[Category!=Budget]&/*/*/*/*[Category!=Gpu]` (top-level `&`) | **497**, second clause dropped | 483 |
| `/*/*/*/*[!(Category=Budget)]` (unary `!`) | **508**, everything | 497 |
| `dotnet test --filter "Category=Budget"` | **everything**, silently | n/a |

Top-level booleans between whole paths are not supported; unary `!` is not a token until MTP 1.8.0,
and `!=` is the only negation that exists here. And `dotnet test --filter` is the worst of the four:
TUnit registers no `--filter` option at all, and in VSTest mode the argument is simply swallowed, so
the command reports success over a full run. **Everything must go through `-- --treenode-filter`.**

One more, for exit codes: **`dotnet test` collapses every platform exit code to MSBuild's `1`**,
which destroys the distinction between "tests failed" (2), "the filter matched nothing" (8) and "bad
arguments" (5). `dotnet run` preserves them, and `scripts/test.sh` reports exit 8 as a tier defect
rather than a pass: a tier whose filter matches nothing is never intended.

### 3.4 `[Explicit]` is banned

`[Explicit]` looks like the natural way to hold a test out of the default run. On 0.25.21 it is a
trap, and `TestTierContractTests.NoTestIsMarkedExplicit` fails the build if one appears:

```csharp
if (testsWithExplicitAttributeCount > 0 && testsWithExplicitAttributeCount < filteredTests.Length)
{
    return testNodes                 // ← ALL tests in the assembly, not filteredTests
        .Where(x => !x.TestDetails.Attributes.OfType<ExplicitAttribute>().Any())
        .ToArray();
}
```

When a filter's match set contains **both** explicit and non-explicit tests, `TestFilterService`
discards the filter and runs every non-explicit test in the assembly instead. One `[Explicit]` test
is therefore enough to turn `-t fast` into a full run, with no error and no warning. The repo has
zero today; the guard keeps it that way. (Fixed upstream, long after this pin.)

### 3.5 Two more sharp edges

- **`[Category!=X]` includes tests with no categories at all**. It means "no `Category` property has
  the value X", not "has a `Category` property that differs". This is exactly what exclusion tiers
  want, and it is why an untagged test is in every tier.
- **Nested test classes appear under the inner name only** (`/*/*/InnerTests/*`), and parameterized
  cases all share one method node; arguments never appear in the path. Both are 0.25.x-specific;
  newer TUnit adds a fifth path segment for cases, at which point `/*/*/*/*` starts silently
  under-matching. Something to re-verify on any TUnit bump.

---

## 4. The taxonomy

**Tiers are defined by exclusion, never by inclusion.** A test with no categories is in every tier, so
a newly written unit test is covered the moment it is written and nobody has to remember to opt it
in. What *costs* something carries a tag, and a tier drops tags.

### 4.1 The vocabulary

Cost tags (at least one tier drops each):

| Tag | Means | Dropped by |
|---|---|---|
| `Budget` | measures rather than asserts behaviour: frame-time and allocation benchmarks | fast, standard |
| `Environmental` | depends on machine or OS state this repo does not own: file-lock semantics, symlink privilege, a per-user settings path, scheduler noise | fast, standard |
| `Gpu` | needs a real GPU render surface (ANGLE/EGL) | fast |
| `Integration` | crosses a host or process boundary: an Avalonia headless application, a web host on a fixed port, a spawned `dv2d` subprocess | fast, standard |
| `RealDemo` | reads a CS2 `.dem` off disk, and usually parses and replays it | fast, standard |
| `Render` | rasterises a production-sized frame, or compares against a committed golden image | fast |

Informational tags (pre-existing, descriptive, no tier reads them): `Unit`, `Probe`.

`Budget`, `Gpu`, `Integration`, `Unit` and `Probe` all predate this phase and keep their exact
meaning and their exact membership. `Budget`'s in particular is load-bearing and was verified by
count (§6.3).

### 4.2 The tiers

| Tier | Drops | For |
|---|---|---|
| `fast` | `Budget` `Environmental` `Gpu` `Integration` `RealDemo` `Render` | the sanity sweep: pure unit and contract tests, no demo, no pixels, no process, no benchmark |
| `standard` | `Budget` `Environmental` `Integration` `RealDemo` | **the in-flight default**: `fast` plus the render and golden gates |
| `full` | *(nothing)* | CI, and a pre-push review |

`fast ⊆ standard ⊆ full` holds by construction because the exclusion sets nest, and
`TestTierContractTests.TierExclusionSets_Nest_FromFastDownToFull` asserts it rather than trusting it,
including that each step is *strictly* widening (two tiers that ran the same set would be one tier
with two names).

### 4.3 Where the tags went

Class-level wherever every test in the class carries the cost, method-level where only some do.
There was no need for a per-method sweep of 900 tests, and no base class to hang anything on (this
repo has none). Assembly-level `[assembly: Category(...)]` was verified to work and deliberately
**not** used: no suite is uniformly expensive, and an assembly tag would have made every tier in that
project either everything or nothing.

| Project | Added |
|---|---|
| `Playback2D.Tests` | `Render` ×6 classes (`SceneGoldenTests`, `LevelGoldenTests`, `GoldenParityTests`, `SceneDeterminismTests`, `SceneSmokeRenderTests`, `SceneRendererTests`); `Environmental` ×1 method (§7.1) |
| `Playback2D.Cli.Tests` | `Integration` ×2 classes + 2 methods (subprocess spawns); `RealDemo` ×2 classes + 3 methods; `Render` ×3 classes |
| `App.Tests` | `RealDemo` ×9 classes + 8 methods (the demo-reading classes not already `Integration`); `Environmental` ×6 methods (§7.2) |
| `DemoTrimmer.Tests` | `RealDemo` ×1 class |
| `LiveSync.Tests` | **nothing**: all 13 classes already carried `Unit` or `Integration`, and the 6 `Integration` ones are exactly the 94-second web-host set |
| `Visualization.Tests` | **nothing**: 29 pure tests |

38 lines added across 34 files, every one of them an attribute. No assertion was touched.

---

## 5. Running them

`scripts/test.sh` is the single entry point and the only place a filter string is written by hand.

```
scripts/test.sh [-t fast|standard|full] [-p PROJECT|all] [-c Release|Debug] [-n] [-l]
```

`-p` keys: `visualization` `trimmer` `livesync` `playback2d` `cli` `app` `all`. Projects run
cheapest-first, so a broken build or an obvious regression surfaces in seconds. `-n` skips the build,
`-l` discovers and counts without running.

The canonical filter strings, for anyone driving the runner directly:

```bash
# fast
--treenode-filter "/*/*/*/*[(Category!=Budget)&(Category!=Environmental)&(Category!=Gpu)&(Category!=Integration)&(Category!=RealDemo)&(Category!=Render)]"

# standard
--treenode-filter "/*/*/*/*[(Category!=Budget)&(Category!=Environmental)&(Category!=Integration)&(Category!=RealDemo)]"

# full
--treenode-filter "/*/*/*/*"
```

`scripts/test-app-suite.sh`, the memory-safe batched runner for the App suite, takes the same `-t`
and composes the tier's bracket onto its class-partition path. Use it instead of `scripts/test.sh -p app`
when the machine is holding real demos and the single process is being OS-killed; its partition audit
now discovers under the tier filter, so a tiered run is no longer mistaken for silent loss.

---

## 6. What it bought

### 6.1 Wall-clock, by tier and project

Same machine, same conditions as §1. Test counts include the 36 new tier-contract tests (6 per suite).

| Project | `fast` | `standard` | `full` | before |
|---|---|---|---|---|
| `visualization` | 35 / 1.8 s | 35 / 1.8 s | 35 / 1.8 s | 29 / 1.9 s |
| `trimmer` | 11 / 0.8 s | 11 / 0.8 s | 18 / 0.8 s | 12 / 1.1 s |
| `livesync` | 60 / **3.3 s** | 60 / **3.3 s** | 68 / 92.6 s | 62 / 94.2 s |
| `playback2d` | 466 / **4.3 s** | 501 / 15.9 s | 514 / 27.2 s | 508 / 32.5 s |
| `cli` | 69 / **3.2 s** | 87 / 6.4 s | 126 / 27.2 s | 120 / 31.4 s |
| `app` | 740 / 24.8 s | 740 / 24.4 s | 911 / 39.0 s | 905 / 44.2 s |
| **all** | **1381 / 40 s** | **1434 / 54 s** | **1672 / 190 s** | 1636 / ≈205 s |

**205 s → 54 s** for the in-flight default across every project, and **205 s → 40 s** for the sanity
sweep. Scoped to one project (the normal in-flight case), `standard` on `playback2d` is **16 s** and
`fast` is **4 s**.

`full` is 1672 = the 1636 that existed before, plus exactly the 36 contract tests. Nothing was
dropped from anything.

### 6.2 Two observations worth keeping

- **`app`'s `fast` and `standard` are the same 740 tests.** The App suite's render and golden work all
  lives in classes already tagged `Integration`, so there is nothing for `standard` to add back. It is
  also the tier's floor: 25 of the 40 seconds of a repo-wide `fast` run are this one suite, and no
  further tagging reduces it: 740 tests at ~33 ms each is bulk, not outliers.
- **`livesync` is where the ratio is spectacular**, 94.2 s to 3.3 s, because 8 of its 68 tests are
  a serialized web-host integration set on a machine-exclusive port. No new tags were needed; the
  `Integration` category that already described them turned out to be the whole answer.

### 6.3 CI is unchanged, by count

The only category any CI lane selects on is `Budget`, and `Category!=Budget` / `Category=Budget` are
complementary, so their union is the whole suite. Verified before and after:

| Suite | `Category=Budget` | `Category!=Budget` |
|---|---|---|
| `Playback2D.Tests` | 11 → **11** | 497 → **503** *(+6 contract tests, nothing else)* |
| `Playback2D.Cli.Tests` | 2 → **2** | 118 → **124** *(+6)* |

`ReviewRegressionTests.TrackerSceneSnapshot_Refresh_AllocatesNoPerFrameDelegate` now carries both
`Budget` and `RealDemo`; categories accumulate, so the budget lane still selects it. The push-to-main
trigger the brief asked for already existed. The only edit to `ci.yml` is a comment explaining all of
the above, in particular that the lanes must **not** be rewritten as `scripts/test.sh -t full`,
because that would collapse the deliberate correctness/budget split.

---

## 7. The guard: tests about the tests

Everything a tier does is exclusion by literal category string, and every failure mode of that design
is silent. A mistyped `[Category("Bugdet")]` compiles, runs, and quietly promotes a benchmark into the
fast tier. Six assertions, compiled into **every** test assembly as linked source
(`tests/shared/TestTiers.cs` + `tests/shared/TestTierContractTests.cs`), close each one:

| Assertion | Catches |
|---|---|
| `EveryCategoryInThisAssembly_IsInTheKnownVocabulary` | typos and undeclared tags; *verified by injecting `[Category("Bugdet")]`: fails* |
| `TierExclusionSets_Nest_FromFastDownToFull` | a tier definition that stops being strictly widening |
| `EveryCostCategory_IsExcludedBySomeTier` | a tag that reads as "expensive" but changes nothing |
| `ScriptTierFilters_AreExactlyTheCanonicalOnes` | `scripts/test.sh` or `scripts/test-app-suite.sh` drifting from `TestTiers.cs`, character for character |
| `EveryClassThatResolvesADemo_IsTaggedOutOfTheDemoFreeTiers` | a new demo-reading class landing in `fast`; *verified by removing the tag from `DemoTrimRoundTripTests`: fails* |
| `NoTestIsMarkedExplicit` | the `[Explicit]` landmine of §3.4 |

Linked source rather than a shared test-support **assembly**, because two suites cannot take the
reference: `Playback2D.Tests` asserts that no Avalonia assembly is even loaded in its process, and
`Cli.Tests` asserts the same of its dependency graph. Source has no graph. The link is declared once
in `Directory.Build.props`, conditioned on `$(MSBuildProjectName.EndsWith('.Tests'))`, so a new test
project gets the guard by existing, "the new project forgot to link the drift check" being the first
way a drift check rots.

The demo guard is honest about its scope: it is per-class, driven by a source scan, and catches a new
demo-reading *class* with no tag. It does not catch a demo-reading *method* added to an
already-tagged class where the tag sits on the siblings. That narrower case is left to review; the
alternative is parsing C# inside a test, which trades a real guard for a brittle one.

### 7.1 One test was retagged, deliberately

`ScenePerfRecorderTests.Reset_RetiresRowsNothingTouchedAfterwards` gained `[Category("Environmental")]`.
Its last line asserts `SharePct` (stage-elapsed over frame-elapsed) is between 99 and 101, so a
thread preempted between `EndStage` and `EndFrame` reports a share below the floor. **Measured at 1
run in 5** with the suite running in parallel on a loaded machine, and never in isolation:

```
total:467 failed:0 · total:467 failed:0 · total:467 failed:0 · total:467 failed:1 · total:467 failed:0
```

A flake in the tier whose entire value is that a red means *your change* is worse than no tier at
all. The tag removes it from `fast` and `standard` and changes CI in no way: CI selects only on
`Category!=Budget`, so it still runs on every pull request exactly as before. Its assertion was not
touched.

### 7.2 The App suite's environmental six

Tagged `Environmental` at method level, so the rest of their classes stay in every tier:

- `DiagnosticsFileLogTests`: `WritesLines_ToActiveFile`, `ReadTail_Works_WhileSinkHoldsFileOpen`,
  `Rolls_AndRetainsAtMostMaxFiles` (Windows file-lock semantics)
- `DemoLibraryServiceTests`: `Scan_DeduplicatesSameFile_AcrossSymlinkedFolders` (symlink privilege),
  `SettingsBacked_AddRemoveFolder_WritesThroughToSettingsJson` (per-user settings path),
  `QueuePath_PersistsCache_SoSecondLaunchDoesNotReparse` (demo-cache queue)

These are the six that fail on this machine and pass elsewhere. They remain in `full`, which is what
a pre-push review runs. The tag says "do not interpret this red as being about your change", not
"stop checking".

---

## 8. Known and deferred

1. **This machine has no large demos.** Only the committed `assets/tour/sample-de_nuke.dem` exists,
   which `Dv2d` finds and `DemoTestHelper` does not, so the App suite's 99 skips are `RealDemo`
   cases that never ran, and `RealDemo`'s contribution to the `full`-tier time is understated here.
   On a machine with a demo library the saving from that tag is much larger than the table in §6.1
   shows; the tag is correct either way.
2. **`Playback2D.Cli.Tests.BenchAllocationTests.SmallestDrawingFixture_AllocatesNothingPerFrame`
   fails locally** (3336 bytes/frame against an expected 0) and passes in CI, which runs it in the
   budget lane with its own environment. Pre-existing, untouched, and out of `fast` and `standard`
   because it is `Budget`.
3. **CI still runs only the Playback2D and dv2d suites.** `LiveSync.Tests`, `Visualization.Tests` and
   `DemoTrimmer.Tests` are in no lane, and the App suite is deliberately excluded (single-process,
   OOM-prone). Tiering makes adding them cheap (`scripts/test.sh -t full -p livesync` is 93 seconds),
   but adding CI lanes is a scope expansion, not a tiering change, and was left out.
4. **Re-verify the filter grammar on any TUnit or MTP bump.** Several behaviours relied on here are
   version-specific in both directions: unary `!` arrives in MTP 1.8.0, the property-matching type
   changes with it (TUnit and MTP must move together), and newer TUnit adds a fifth path segment for
   parameterized cases that would make `/*/*/*/*` under-match. §3 is written to be re-run, not
   trusted.

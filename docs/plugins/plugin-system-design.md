# Add-on System: Research and Design

Draft for owner review. Rev. 2, 2026-08-20 (owner corrections folded in; see §8 for the revision
record and what changed).

**Scope, per owner direction:** third-party **UI pages and the features that accompany them**.
Explicitly *not* extending the CSVG live-sync integration, and *not* a richer rules/declarative tier.
That narrowing is load-bearing: it removes most of the design space and forces the central conclusion
in §2.1.

**Nothing here is implemented.** This is a design artifact; every code reference describes the tree as
it stands at `a8f24df` (0.7.1).

**Naming.** "Plugin" is already taken in this product: it means the **CSVG native CS2 game plugin**
that live-sync installs into the user's Steam install (`docs/distribution/build-and-packaging-plan.md:132`,
`LiveSync/InstallRecovery.cs:39`), and it appears in shipped user-facing copy
(`docs/ui/design-system.md:538-542,1386-1391`). Overloading it would be a lasting readability tax on
both the code and the UI. **This document uses "add-on" for the third-party extension unit** and
reserves "plugin" for the CS2 game plugin. The existing code name for the in-app unit is **module**
(`IWorkspaceModule`), which stays.

---

## 1. Current state

Three extension seams exist. Exactly one of them is usable by a third party today, and, given the
narrowed scope, none of them can produce a UI page without a loader that does not yet exist.

### 1.1 The module framework: a clean contract wired to a hardcoded list

`src/App/DemoViewer.NET.Modules.Abstractions/` is a genuinely well-shaped contract assembly. It targets
`net10.0`, references exactly one package (Avalonia), and has no CS2DemoKit reference at all; its own
csproj comment states the intent: *"The stable third-party reference target for workspace modules.
Deliberately MINIMAL … Keeping abstractions clean is what makes it a versionable SDK target."*

`IWorkspaceModule` (`IWorkspaceModule.cs:9-28`) asks for `Id`, `DisplayName`, `ContractVersion`, and
`CreateTabs(IModuleHost)`. `IModuleContext` (`IModuleContext.cs:9-164`) is the runtime object, and its
doc comment states the design principle:

> READ-ONLY, push/observable, render-frame-coalesced. It deliberately does NOT expose the live
> `EntityTracker`, the raw byte buffer, the `DemoParser`, or any mutator: a module simply has no API
> to corrupt state (the primary, real guardrail).

That claim is accurate as far as the *interface* goes. Entity fields cross the boundary boxed as
`object?` keyed by dotted path (`IReadOnlyEntity.cs:27`); game-event fields as
`IReadOnlyDictionary<string, object?>` (`GameEventView.cs:22`); the de-boxing happens host-side
specifically so the abstraction stays parser-free (`Modules/GameEventViewFactory.cs:11-19`). No
CS2DemoKit type appears in any signature. §2.2 explains why the parenthetical, *"the primary, real
guardrail"*, is nonetheless not true as a security claim.

**What is not true today is everything around it.**

**There is no discovery and no loading.** `ModuleRegistry` (`Modules/ModuleRegistry.cs:14-31`) is a
`List<IWorkspaceModule>` with a `Register` that de-dups by `Id`. It is populated by hand in the
composition root: `App.axaml.cs:772-805` news up `Playback2DModule`, `RuleWorkbenchModule`, and
`HighlightsModule`; `BuiltInTabsModule` registers itself from the shell at `MainViewModel.cs:2174`.
There is no `Assembly.Load`, no `AssemblyLoadContext`, no `LoadFrom`, no MEF, no DI assembly scanning,
and no `*.dll` globbing anywhere in `src/`. A third party's only path is to fork the repo and add a
line to `BuildRegistry`. **This is the entire gap between today and the owner's requirement.**

**No module is a separate assembly.** `DemoViewer.NET.slnx` lists seven `src/App` projects and none of
them is a module. Highlights, Library, Playback2D, and RuleWorkbench are *folders* inside
`DemoViewer.NET.dll`, and each freely references CS2DemoKit, the shell view-models, and concrete
Avalonia views: `Playback2DModule.cs:45` does `new Playback2DView()`, `BuiltInTabsModule.cs:34,42`
takes the shell as `object` and assigns it as `DataContext`. The abstraction has therefore **never been
exercised across an assembly boundary**. That is not a criticism of the design; it is a warning that
its first real out-of-assembly load will surface problems that in-assembly use cannot reveal, and a
reason to make first-party modules the first consumers of the packaged contract (§5, Phase 2).

**The capability system is cosmetic.** This is the most important correction to any mental model of the
current state. `ModuleHost.FirstPartyCapabilities` (`ModuleHost.cs:29-33`) defines a real vocabulary:
`Demo.Read`, `Entities.Read`, `Analysis.Read`, `Playback.Observe`, `Playback.Control`, `UI.Contribute`.
and the class comment even anticipates the third-party default set. But:

- `HasCapability` has **zero call sites** outside its own declaration and implementation.
- `MainViewModel.cs:2406-2407` is `HostCapabilitiesFor(IWorkspaceModule module) => ModuleHost.FirstPartyCapabilities;`
  The parameter is ignored and every module receives all six.
- `IModuleContext.RequestSeekToFrame` and friends are documented as *"capability-gated"* and *"no-op
  without the grant"* (`IModuleContext.cs:92-93`), but `ModuleContext.cs:113-116` calls
  `_controller.SeekToFrame(...)` unconditionally, with no check.

One `ModuleContext` instance is shared by every module (`MainViewModel.cs:2176-2191` passes
`_moduleContext` to each `ModuleHost`), so per-module enforcement needs a per-module wrapper, not just
an `if`.

**`ContractVersion` is written by every module and read by nobody.** There is no version negotiation of
any kind.

**The abstraction already has a documented escape hatch.** `Modules/ICurrentDemoSource.cs:18-22`
exposes `ParsedDemo? CurrentDemo` (a CS2DemoKit.Parser type) and `ModuleContext` implements it
(`ModuleContext.cs:33`). The documented access pattern is `context is ICurrentDemoSource`. Today this is
contained because the interface lives in the app assembly, but it is a downcast-to-get-the-real-object
door in the middle of the read-only story.

**Failure isolation is partial.** `CreateTabs` is wrapped in try/catch so *"a misbehaving module never
crashes the shell"* (`MainViewModel.cs:2182-2190`), but `ViewFactory()` and `Activate()` are not.

**The feature gate fails open.** `MainViewModel.cs:95-100` maps `TabId → featureId` in a hardcoded
dictionary and `IsTabEnabled` (2226-2241) shows any tab it does not recognise. A third-party tab would
be ungated by default.

### 1.2 Rules v2: real, safe, and now out of scope

Data-driven YAML rulesets are the only thing a third party can ship today without forking. Shipped set
is 15 files in `rules/`; users get `<config>/rules/`, resolved by
`CS2DemoKit.Analysis/Yaml/RuleSetLocator.cs` (165-175). A user ruleset with the same id **replaces** the
shipped one (`YamlConfigLoader.MergeById`, 503-528); a new id appends. Shipped-tier errors throw;
user-tier errors are collected and contained (488).

The security posture is unusually strong and deliberate. The spec's charter states the goal as *"an
owned language with CEL's discipline: a published EBNF, a typed checker …, no Turing-completeness, a
closed function set, exception-free evaluation, provable purity, and statically enumerable read sets"*
(`docs/rules-v2/rules-v2-spec.md:32-36`). Verified: the function set is closed at six
(`Analysis.Rules/Ast/Operators.cs:66-88`), exhaustively type-checked with a throwing default arm
(`Checking/ExpressionChecker.cs:660-745`). No user-defined functions, no lambdas, no loops, no I/O.

The compiler lowers rulesets to LINQ expression trees via `Expression.Lambda(...).Compile()`
(`Analysis/Building/ExpressionCompiler.cs:200,206,222,…`): runtime IL generation, but from an AST the
library builds itself out of a closed node and function set. It is not Roslyn. **A malicious ruleset
cannot execute arbitrary code. It can still burn CPU and allocate**, which is DoS, not compromise.

**Under the narrowed scope this tier is informational only.** Rules v2 produces statistics and
highlights; it cannot produce a UI page. It remains the right home for *stat and highlight* extensions
and its hardening items are still worth doing (§5, Phase 0), but it is not an answer to the owner's
requirement and this document no longer proposes investing in it as one. See §8 for what that changed.

### 1.3 Themes: the working precedent for a data-only drop-in registry

`<config>/themes/*.json` is a live, third-party-usable extension point nobody calls a plugin system.
`ThemeRegistry.LoadUserThemes` (`Theming/ThemeRegistry.cs:175-230`) is the pattern any add-on discovery
loop should copy: tolerate a missing or unreadable directory, enumerate in deterministic ordinal order
(198) so behaviour is stable, skip malformed files rather than failing the batch (214-218), and **refuse
to let a user id shadow a built-in id** (223-226). `AppPaths` supplies the matching path convention, a
pure side-effect-free getter plus a best-effort `EnsureThemesDirectory()` called once at startup,
returning `null` on WASM (`Services/AppPaths.cs:145-200`).

### 1.4 `CS2DemoKit.Analysis.Plugins` is not a plugin system

The namespace consumed by `ModuleContext.cs:3` is a set of static analysis helpers: `PawnLookup`,
`PositionUtil`, and the `IEntityValueProvider` family, registered via a hand-populated dictionary
(`EntityValueProviderRegistry.CreateDefault()`, 17-22). There is no assembly loading or type scanning
anywhere in `C:\dev\CS2DemoKit\src`. "Plugins" here is a namespace name and nothing more.

### 1.5 Build and distribution constraints: better than expected

The single most consequential fact: **the shipped app is untrimmed, non-AOT, non-single-file, and
non-ReadyToRun**, by documented decision rather than oversight.
`docs/distribution/build-and-packaging-plan.md:199-208` lists full-app NativeAOT and full trimming under
*"Explicitly rejected (don't revisit without new information)"*. A repo-wide grep confirms
`PublishTrimmed`, `TrimMode`, `PublishAot`, `PublishSingleFile`, and `PublishReadyToRun` appear in no
csproj, props, targets, script, or workflow. The only publish command is `scripts/publish.sh:81`, with
no `-p:` overrides.

This matters because trimming is the standard reason .NET desktop apps cannot support add-ons: the
trimmer removes framework code the host does not itself call, and an add-on calling it fails at run time
with `MissingMethodException` on whichever path the user happens to hit. **That failure mode does not
apply here.** In-process add-on loading is available on desktop today with zero build-configuration
changes, the single biggest thing working in favour of the owner's requirement.

Other constraints that shape the design:

- **RIDs**: `win-x64`, `osx-arm64`, `linux-x64`, self-contained, per-RID, refusing cross-build
  (`pack-velopack.sh:45-55`). ~58 MB unzipped.
- **Velopack replaces `current/` on every update** (`build-and-packaging-plan.md:136-138`, and Velopack's
  own preserved-files documentation). `AppContext.BaseDirectory` **is** `current/`. Add-ons dropped next
  to the executable would silently vanish on the first auto-update. They must live under
  `AppPaths.ConfigRoot`, alongside `themes/` and `rules/`, which survive both update and uninstall.
- **Builds are unsigned.** README:25 says so; `pack-velopack.sh:127-132` wires
  `--signAppIdentity`/`--notaryProfile` behind env vars CI leaves unset (`release.yml:5-7`). There is no
  signing infrastructure to reuse for add-on signature verification.
- **CS2DemoKit has no cross-version compatibility contract.** `Directory.Build.targets:50-62` pins exact
  intra-family versions and explains why: *"The family is versioned in lockstep and shares no
  compatibility contract across versions, so the floor range is a lie that surfaces as a runtime
  `MissingMethodException`."* **Decisive: add-ons must never reference CS2DemoKit** (§4).
- **The abstraction assembly is not shipped.** `Directory.Build.props:28` sets `IsPackable=false`
  repo-wide. The pack infrastructure in `Directory.Build.targets:19-65` already exists and would apply on
  a flip to `true`.
- **Browser/WASM cannot host add-ons.** The head is `Microsoft.NET.Sdk.WebAssembly` with
  `RunAOTCompilation` unset (interpreter mode, the *favourable* case), but it is not
  `Microsoft.NET.Sdk.BlazorWebAssembly`, so `LazyAssemblyLoader` is unavailable; runtime assembly loading
  there means `INTERNAL.loadLazyAssembly`, an unsupported internal surface. It is **not built by CI**
  (`ci.yml:3-4` says so explicitly) and not deployed. **Add-ons are desktop-only. Say so plainly in
  docs.**

### 1.6 Ambient issues that become materially worse with third-party code

Three pre-existing items, independent of add-ons, that the narrowed scope does not remove.

**`AppHostHooks` is three public mutable statics** (`Services/AppHostHooks.cs:33,41,53`):
`LiveSyncFactory`, `ReelJobFactory`, and, most seriously, `UpdateServiceFactory`. Anything running in
the process can overwrite them. Replacing the updater factory is a persistence and
remote-code-execution primitive. **These must be write-once or internal before any dynamic loading
ships.**

**LiveSync exposes an unauthenticated local gRPC endpoint.** `CsvgWebHost.cs:41` fixes the port at 50051
(documented as *"the fixed plugin dial-back port"*) and `:112-113` binds `ListenLocalhost` with
`HttpProtocols.Http2`: h2c, no TLS, and no authentication or authorization registered anywhere in the
file. Grepping the LiveSync project for `Authentication|Authorization|Credential|Interceptor` returns
only `CancellationToken` matches.

**Stated precisely: `ListenLocalhost` binds loopback only, so this is not network-exposed.** The
exposure is to *any local process running as the user*, which, once add-ons load in-process, includes
every add-on, but also already includes any other software on the machine. An impersonating local
process can feed the app fabricated game state or race for the command channel that drives CS2. The
hermetic configuration (`:89-90` clears all config sources) shows the file was written carefully;
caller authentication is the missing piece, and a per-session shared secret is cheap and sufficient.

**Post-crash, the user's CS2 may not launch, and the repair path does not say so.** Live-sync patches
the CS2 install; `InstallRecovery.cs:14-19` documents that a DV crash with a live session skips
`ShutdownRequested`, leaving `gameinfo.gi` patched so *"the plugin loads on every NORMAL CS2 launch."*
Per the owner, the CSVG plugin enforces `-insecure` at load and **CS2 will not launch without it**, so
the post-crash failure mode is not a ban risk (the enforcement is fail-closed, in the right place), it
is that **the user's normal Steam launch of CS2 is blocked until the leftover is restored.**

Recovery exists and is reachable: detection runs at desktop start (`AttachLiveSync` is unconditional at
`App.axaml.cs:98-102`; the probe fires in the status VM constructor, `LiveSyncStatusViewModel.cs:173`),
and anyone who could have hit this necessarily enabled `chrome.livesync`, so the chip is visible to
them. But the offer sits inside the Live Sync flyout, and its copy
(`Views/LiveSync/LiveSyncStatusView.axaml:65`) frames the problem as untidiness, *"A previous session
left your CS2 install modified … Restore it now — no CS2 launch needed"*, and never as *"this is why
your game will not start."* A user whose game stops launching has no reason to connect the symptom to
DemoViewer, let alone to a flyout.

**Proportionate fix:** reword the copy to name the symptom, and consider restoring automatically at
startup with a notice rather than offering it. Small, cheap, and unrelated to add-ons.

### 1.7 Summary of the gap

| Seam | Third party can do today | What stops them | What must change for UI add-ons |
|---|---|---|---|
| `IWorkspaceModule` | Nothing without forking | No discovery, no loader, no shipped SDK package | **Everything**: loader, drop folder, `IsPackable`, contract versioning |
| Capabilities | Everything; all six granted, none checked | Nothing | Per-module grants and a per-module context wrapper |
| `ICurrentDemoSource` | n/a (contained by assembly boundary) | Assembly boundary only | Gate or remove before out-of-assembly loading |
| Rules v2 | Ship rulesets, override shipped ids | Closed, pure, statically-checked DSL | Out of scope: cannot produce a UI page |
| Themes | Restyle the app | Data-only, id-shadow protected | Nothing: copy this discovery pattern |
| `AppHostHooks` | n/a | Assembly boundary only | Lock down before dynamic loading |

---

## 2. Threat model and security posture

### 2.1 The requirement forces full trust. This is the central finding.

The owner wants third-party **UI pages**. That single requirement determines the security model, before
any threat analysis, because of one signature:

```csharp
public required Func<Control> ViewFactory { get; init; }   // WorkspaceTabDescriptor.cs:73
```

An add-on that owns a UI page must hand the host a live Avalonia `Control`. A `Control` is a managed
object with a live visual-tree parent, resolved styles, and a dispatcher affinity to the UI thread. **No
isolation technology can produce one from outside the process.** A child process cannot return a
`Control`. A WASM module cannot return a `Control`. An OS sandbox around a separate process cannot
return a `Control`. Serialising a UI description across a boundary is a different architecture; it
means the host renders and the add-on never owns a page, which is not what was asked for.

Therefore: **for third-party UI pages, full trust is not a choice among options. It is a property of the
requirement.** Every isolation option surveyed in this research (§2.6) is rejected not on cost or
maturity grounds but because none of them can satisfy the seam.

This is the most important sentence in the document, and it should be said to users too, not just
recorded here: **an add-on that draws a page can do anything the application can do.**

### 2.2 What "full trust" actually means, on the record

It is worth stating precisely, because the current contract implies otherwise.

Code Access Security (the mechanism that once made partial trust plausible) is gone. Microsoft's
documentation ([Code Access Security][cas], archived under `/previous-versions/`) states: *"CAS is not
supported in .NET Core, .NET 5, or later versions"*, and *"CAS and Security-Transparent Code are not
supported as a security boundary with partially trusted code, especially code of unknown origin."*

The .NET team has declined to replace it, on the record. On [dotnet/runtime#4108][r4108], Jan Kotas:
**"we have no plans to support secure sandboxing of untrusted code in the CoreCLR runtime"**; Dan
Moseley: *"Attempts to create a trust boundary within a process have repeatedly been defeated. It's not
just .NET, browsers try to avoid it too."* On [dotnet/roslyn#10830][r10830], Tomáš Matoušek: **"any
in-process sandbox can be circumvented and is not secure"**; the recommended answer is *"Run the code in
an isolated process."* Microsoft's plugin tutorial: *"Untrusted code cannot be safely loaded into a
trusted .NET process."*

Concretely, an add-on can P/Invoke and `NativeLibrary.Load` arbitrary native code; reflect over every
`private` and `internal` member (`BindingFlags.NonPublic`, and `[UnsafeAccessor]` since .NET 8 makes it
zero-cost); call `File.*`, `Socket`, `HttpClient`, `Process.Start`; read and write process memory via
`Marshal`/`Unsafe`; `Assembly.Load(byte[])` code that was never reviewed; and patch the host via
reflection, detouring, `DOTNET_STARTUP_HOOKS`, or the profiler API. `internal`, `sealed`, `private`, and
`InternalsVisibleTo` provide **zero** defence. `AssemblyLoadContext` is a *versioning and unloadability*
mechanism: the runtime docs are explicit that *"there's no binary isolation between these dependencies;
they're only isolated by not finding each other by name."*

**So the read-only `IModuleContext` is not a security boundary and must stop being described as one.**
Its doc comment calls the read-only surface *"the primary, real guardrail"* (`IModuleContext.cs:9-14`).
That is an API-shape argument, and it is already false in-assembly: `ReadOnlyEntityView` is `internal
sealed` with a `public void Aim(EntitySet)` and is a **single mutable instance shared by every module**
(`ModuleContext.cs:39,249`): one reflection call re-points what every other module reads. Single
lookups return a shared pooled facade, so retaining it past the callback reads whatever was looked up
next. And `ViewFactory` hands back a live `Control`, from which `Application.Current`, `TopLevel`, the
clipboard, and the storage provider are one property access away; no reflection required.

Those are correctness bugs worth fixing on their own merits (§5, Phase 1). Fixing them does not create a
boundary. **The contract's value is preventing accidents, not attacks**, and that is a genuinely
worthwhile thing for it to do, as long as we say which one it is.

### 2.3 What is worth stealing here

| Asset | Why an attacker wants it |
|---|---|
| `.dem` libraries | Scrim and pro-team demos are confidential strategic material; competitive espionage is a plausible motive in CS esports |
| Demo paths + SteamID64s + player names | **Available through the *sanctioned* API alone**: `DemoPath` plus `PlayerRosterEntry` for all ten players in every demo opened |
| CS2 install and `cfg/` | Config tampering; the `gameinfo.gi` seam is a proven code-load path |
| Steam session material, browser cookies, SSH keys, documents | Standard infostealer targets; reachable by any in-process code |
| The auto-updater (`AppHostHooks.UpdateServiceFactory`) | Persistence and a delivery channel for later stages |

**The second row reframes the consent UX.** Even a well-behaved add-on granted only the contract we
already ship accumulates a competitive-intelligence dataset across every demo the user opens. Since
technical egress control is impossible in-process (§2.1), **this must be disclosed at install time
rather than mitigated**: the honest statement is "add-ons see every demo you open, including who played
in it."

### 2.4 Threat table

Isolation-based mitigations are absent by construction; what remains is consent, provenance,
revocation, blast-radius limitation, and detection.

| Adversary | Attack | Likelihood | Impact | What actually helps |
|---|---|---|---|---|
| **Negligent** add-on | Crash, hang, leak, corrupt the shared entity view | **Highest** | Med | Failure isolation around `ViewFactory`/`Activate`; watchdog + circuit breaker; per-module entity views |
| Malicious add-on | Credential/cookie theft, demo exfiltration, ransomware, mining, persistence | Med | Critical | **Nothing technical.** Consent, provenance, revocation, and the fact that it is attributable |
| Malicious add-on | Harvest demo paths + SteamIDs via sanctioned API | High | Med | Disclosure at install; this is granted, not stolen |
| Malicious add-on | Overwrite `AppHostHooks.UpdateServiceFactory` → own the updater | Med | **Critical** | Make those statics write-once/internal **before** any loading ships |
| Malicious add-on | Reach the LiveSync gRPC channel and drive CS2 | Med | High | Out of add-on scope by owner direction; authenticate the channel regardless (§1.6) |
| Supply chain | Popular add-on updates to a malicious v1.1; typosquat; maintainer account takeover | Med | Critical | Author identity, immutable artifacts, revocation, no remotely-hosted code (§2.7) |
| Malicious demo file | Parser bug on untrusted binary input | Med | Med | Fuzz the parser; unchanged by add-ons |
| Malicious ruleset YAML | Unbounded recursion → uncatchable `StackOverflowException` | High | Med | Bump YamlDotNet (§2.5) |

The **negligent** add-on is the most likely adversary by a wide margin and the one worth engineering
against, because it is the only one where in-process mitigations actually work. An add-on can ignore a
`CancellationToken`, `Thread.Abort` is gone, and `StackOverflowException` is uncatchable, so we can
*detect* a hung add-on, stop calling it, and tell the user which one it was, but we cannot reclaim the
thread. Naming the culprit is most of the value: it turns "DemoViewer is broken" into "this add-on is
broken", which is both true and actionable.

### 2.5 Two live bugs found during this research

Both are independent of add-ons and worth fixing regardless.

**YamlDotNet 16.3.0 is pinned (`Directory.Packages.props:24`) and carries an uncatchable process-kill
DoS.** [YamlDotNet #1109][yaml1109]: `new Deserializer().Deserialize<object>(new string('[', 100_000))`
→ `StackOverflowException`, uncatchable, kills the process. Fixed in **18.1.0** by a default max
recursion depth of 130. The app auto-loads user rulesets from `<config>/rules/` without prompt or
signature, so this is reachable today. **Recommend bumping to ≥ 18.1.0.**

A correction to a common assumption while we are here: **YamlDotNet is not SnakeYAML.** The
arbitrary-type-instantiation class of bug (CVE-2018-1000210) was fixed in 5.0.0 by dropping tag-based
type resolution; the default resolver chain ends in `PreventUnknownTagsNodeTypeResolver`. **Our YAML
exposure is DoS, not RCE**, provided nobody registers `TypeNameInTagNodeTypeResolver`. Worth a test.

**The unauthenticated localhost gRPC endpoint**, described precisely in §1.6: loopback-bound, so
local-process reachable rather than network-exposed. A per-session shared secret closes it.

### 2.6 Why there are no isolation options for this seam

This section was originally an options survey. Under the narrowed scope it is **rejected-alternatives
material**, retained because it is the evidence that full trust is forced rather than chosen. Each
option below is rejected for the same root reason (*it cannot return an Avalonia `Control`*) with the
secondary costs recorded so nobody re-opens them hoping the secondary costs were the only problem.

**Out-of-process + OS sandbox.** The only real boundary, and the only one Microsoft endorses. Cannot
return a `Control`. Secondary costs, had that not been fatal: **macOS is a blocker**: App Sandbox needs
entitlements, which need code signing, and we ship unsigned; the packaging plan additionally intends to
keep App Sandbox *off* to preserve file access and process launch. Linux **Landlock** is the bright spot
but has no .NET binding. On Windows, Job Objects cap resources without confining capability, and low
integrity blocks *writes* up, not *reads*, so it would not stop demo theft anyway. Separately, per-tick
IPC is a non-starter: at ~50 µs per round trip a 64,000-tick demo is seconds of pure overhead.

**WASM/WASI.** Cannot return a `Control`. Secondary costs: `wasmtime-dotnet` **cannot instantiate
components** (tracking issue open since July 2024; the community WASI 0.2 PR was closed unmerged in June
2026), C# cannot practically compile to a wasm component (`wasi-experimental` removed;
`componentize-dotnet` is a NativeAOT-LLVM preview with no macOS support), and security patching would be
gated on an annual NuGet cadence. Also: **every host function we import is a hole we punched
ourselves**: a `read_file(path)` import re-grants ambient authority with none of WASI's path sandboxing.

**In-process sandboxing of managed code, in any form.** Not possible; §2.2, on the runtime team's own
record.

**A restricted embedded VM (Lua/JS) as the UI-page host.** Cannot return a `Control` without a full
UI-binding layer, at which point the bindings *are* the attack surface. The prior art is unusually
clear and worth keeping because it generalises: SourceMod built a genuine VM (pointer-free, bounded
memory, a real bytecode verifier, watchdog timers) and then shipped `ServerCommand` (documented as *"as
if it were on the server console (or RCON)"*) and a `file://` absolute-path escape in `files.inc`.
HLAE's mirv-script picked an embedded JS engine and, over ~18 months of good-faith convenience
additions, grew `mirv.exec`, arbitrary outbound WebSockets, and unrestricted filesystem access, and now
ships a warning that scripts *"can have a huge degree of control over your PC."* **The architecture is
not the safeguard; the written-down, defended capability list is.** Valve's `cs_script` (Sept 2025) is
the disciplined counter-example: per-script isolated globals, a curated host API with no filesystem,
network, or process surface, `.d.ts` as the versioning contract.

**Capability manifests as an enforcement mechanism.** For full-trust code they are disclosure, not
enforcement, and the honest citations are competitors admitting it. Obsidian: *"Due to technical
limitations, Obsidian cannot reliably restrict plugins to specific permissions or access levels."* VS
Code: *"The extension host has the same permissions as VS Code itself."* Blender's permissions are
advisory; an add-on can `import socket` regardless; the strings exist for review and store disclosure.

**But there is a narrow band where a manifest *is* enforceable for us, and it is worth building.**
Host-mediated operations that go through `ModuleContext` (`RequestSeekToFrame`, `RequestPlay`,
`RequestPause`, `NotifySpectateTarget`, `RequestNextEvent`) can be genuinely gated, because the host
owns the implementation. That makes the existing doc-comments true, costs a per-module wrapper, and
gives the consent UI something concrete to describe. Be equally explicit that
`Demo.Read`/`Entities.Read`/`Analysis.Read` are **disclosure only**: an add-on with a `Control` reads
whatever it wants. A manifest that mixes enforced and advisory capabilities without labelling which is
which is worse than no manifest, because it teaches users that the labels mean something.

Blender's best idea is worth copying regardless: every declared capability carries a **mandatory
human-readable reason string** shown at install: `"Playback.Control": "Seeks to the moment you click on
the map"`.

**Curation.** Does not scale to a small team, and fails at large ones. Obsidian's plugin guidelines are
a *code-quality* review whose single security item is an `innerHTML` lint. VS Code runs AV plus sandbox
detonation plus verified publishers, and researchers still typosquatted the 7M-install "Dracula
Official" theme as "Darcula", **registered a domain to obtain verified-publisher status**, and infected
100+ organisations in 24 hours; the same scan found 1,283 extensions with known malicious code totalling
229M installs. Blender's ~30-item manual checklist is 20–45 minutes of skilled time *per submission and
per update*, sustainable only because it is crowdsourced to volunteers. **We do not have volunteers.**

### 2.7 Supply chain: now the centre of gravity

With isolation off the table, the residual risk concentrates here, and this is where engineering effort
actually buys safety.

**The downloader pattern defeats review, and the industry has conceded the point.** **fractureiser**
(Minecraft/CurseForge, 2023) is the closest analogue: stage 0 inside the mod JAR was *almost empty*: a
`URLClassLoader` pointed at an IP address. Static review reveals a URL, not malware. Later stages stole
game, Discord, browser, and payment credentials, and **self-propagated by injecting a fresh stage 0 into
every `.jar` on the filesystem**, so infected mod developers silently shipped infected builds.

Chrome's response was to ban remotely-hosted code outright: *"all of your extension's logic must be part
of the extension package."* Remote *data* is fine; the line is executable code. **Adopt that rule from
day one, far easier to state before an ecosystem exists than after.** Obsidian independently bans
self-updating plugins, and for us that has an operational reason too: a self-updating add-on defeats
both the staging pipeline (§3.3) and the blocklist.

Two mechanisms worth internalising. **Cyberhaven (Chrome, Dec 2024)**: no credentials were phished;
victims authenticated to *real* Google and consented to a malicious OAuth app requesting publish rights.
**2FA is irrelevant against a legitimate consent grant.** **Shai-Hulud (npm, 2025)** exfiltrated to
public GitHub repos *in the victim's own account*, defeating egress filtering and C2 blocklists.

**On revocation, SourceMod's blocklist is a decade-long natural experiment and its shape is the
lesson.** ~100 MD5 hashes accumulated 2012–2020. Reading the pattern rather than the names: whole
*author catalogues* were revoked one hash at a time (~40 entries for a single author) **because there
was no identity to revoke**; versioned hashes are defeated by a whitespace recompile; the blocklist is
*optional* by config; it doubles as licence enforcement, muddying both purposes; and one entry reads
simply `Unknown VIP plugin with fake myinfo`: **metadata is self-declared and forgeable**. If we ever
need to revoke, we need **author identity, not artifact hashes.**

For the revocation channel, the privacy-preserving design is Google Safe Browsing's: ship a local
database of **4-byte hash prefixes**, so a user with no revoked add-ons generates *zero* network
traffic, ever, and a prefix hit reveals only an ambiguous prefix.

---

## 3. Recommended architecture

### 3.1 The primary recommendation

**Build one add-on tier: in-process, managed, full-trust, desktop-only, off by default, behind explicit
informed consent, and put the engineering effort into the things that actually work at full trust,
which are the manifest, consent UX, distribution integrity, revocation, and blast-radius limitation.**

Do not build tiers for symmetry. The narrowed scope supports one code tier; the declarative content that
exists (themes, rulesets) is not a "tier 0" of this system, it is a separate, already-shipping thing
that happens to be safe.

Sequencing matters more than mechanism here, and the recommended order is deliberately
loader-last:

1. **Fix the honesty bugs in the existing contract** (capabilities, shared mutable views, failure
   isolation). Cheap, independently valuable, and it makes the contract describable to third parties.
2. **Ship the contract as a versioned package with mechanical API gates, before shipping any loader.**
   People can build against it, first-party modules become the first cross-assembly consumers, and we
   learn what add-on authors actually want before we commit to a trust and distribution model.
3. **Then ship the loader with consent, provenance, and revocation together.** Not consent first and
   revocation "later": a kill switch retrofitted after an ecosystem exists is a kill switch that
   arrives after it was needed.

### 3.2 The shape of an add-on

**Manifest + assembly.** A declarative manifest describes identity, compatibility, contributions, and
capabilities; the assembly supplies the code. The host reads the manifest **without loading the
assembly**, which buys three things: the tab strip renders without executing add-on code at startup, an
incompatible add-on is rejected before it can run anything, and the consent prompt can describe the
add-on accurately before any of it executes.

`WorkspaceTabDescriptor` is **already declarative-shaped** (`TabId`, `Header`, `Icon`, `Order`,
`Placement`) with a lazy `ViewFactory`. The change is to lift those five fields into the manifest and
keep the `Control` factory as the code-side escape hatch. That is a small change to an existing design
rather than a new architecture.

Honest limits of the declarative half: it cannot express UI that is not a pre-enumerated shape, hence
the `Control` escape hatch. The split is a cliff, not a ramp: an add-on either contributes a descriptor
or needs the runtime `IModuleContext`. And any `when`-style predicate in the manifest **must resolve
entirely from host-owned state** (`hasDemo`, `mapName`, `isPlaying`); VS Code's most common
expressiveness complaint is context keys the host does not publish, which extensions can only simulate
by activating first, defeating the premise. Publish a small closed vocabulary or none at all.

**Loading mechanics.** Collectible `AssemblyLoadContext` per add-on, an **explicit shared-types list**,
discovery from `<config>/add-ons/` (never next to the executable, since Velopack replaces `current/`),
`MetadataLoadContext` for inspect-before-load, and a load-time reject for any assembly whose
`AssemblyRef` table names `CS2DemoKit.*` (§4).

**CounterStrikeSharp** is worth reading before writing this: a .NET 8 plugin framework for CS2 servers
(~1.3k stars) doing exactly this in a hot-path game server via `McMaster.NETCore.Plugins`, with
`IsUnloadable = true`, `PreferSharedTypes = true`, and an explicit shared-types array. **That array is
the type-identity problem solved at the API level** instead of relying on every author getting the
csproj right, the failure mode Microsoft's own tutorial warns about, which manifests as **silent
non-discovery** rather than an error.

Three costs to state plainly:

- **The contract-reference incantation is load-bearing and easy to get wrong.** For a NuGet-distributed
  contract the correct form is `<PackageReference ... ExcludeAssets="runtime" />`, keeping `compile` so
  types resolve, dropping `runtime` so the DLL is neither copied to `bin/` nor listed in `deps.json`. For
  in-repo `ProjectReference` you need **both** `<Private>false</Private>` and
  `<ExcludeAssets>runtime</ExcludeAssets>`. **The host must not trust the author to get this right**:
  hence the shared-types list, plus a loud, specific error if an assembly references the contract but
  yields no `IWorkspaceModule`.
- **Unload is best-effort, never guaranteed.** Our own code supplies the first leak source:
  `ModuleRegistry` is an app-lifetime DI singleton holding a strong reference to every module. Worse, and
  flagged as **needing a spike before we promise anything**: `AvaloniaProperty.Register<TOwner, TValue>`
  writes into a global registry keyed by owner `Type`, which roots the add-on's `Type`, which roots the
  `LoaderAllocator`. If that holds, any add-on registering a styled property can never unload.
  Collectible ALCs also **ignore ReadyToRun and run JIT-only**. Treat unload as an optimisation, never a
  dependency.
- **On Windows a loaded DLL is locked.** Combined with cooperative unload, **the only reliable
  install/update/uninstall path is stage-now-apply-on-restart**: download → verify hash → write to
  staging → record a pending operation → apply at startup before any ALC loads. **Ship this in v1;
  retrofitting it is painful.**

### 3.3 Where the security effort actually goes

Since isolation is unavailable, these five are the whole security programme. This is a harder and more
interesting problem than tiering was, and it deserves the budget that would have gone to a sandbox.

**Consent.** Off by default. A restricted mode in the Blender/Obsidian style, where add-ons are present
but inert until the user turns the capability on once, and each add-on is enabled individually. The
prompt must state the truth from §2.1, *this add-on can do anything DemoViewer can do, including read
your files and use your network*, not a permission list that implies containment. Per-capability reason
strings for the host-mediated operations that are genuinely gated.

**Provenance and identity.** Author identity is the thing revocation needs (§2.7), so it must exist from
the first release. Minisign (Ed25519, no PKI, a public key is one line) is the right weight for a small
team; Sigstore's .NET client is an 8-star personal repo and TUF has no .NET implementation. What signing
buys is **who to blame and what to revoke**, not prevention. npm's own provenance documentation says it
plainly: provenance *"does not guarantee the package has no malicious code."*

**Distribution integrity.** Recommended: **NuGet as the artifact store, a small signed JSON index in a
GitHub repo as the catalog, nothing self-hosted.** NuGet gives CDN delivery, **immutability by policy**,
repository signing, semver resolution, and ID prefix reservation. The index carries
`{id, publisher, nugetId, version, sha256, hostApi, capabilities}` plus a version-ranged blocklist; the
client verifies the hash and refuses on mismatch. The client must never call `api.github.com`, whose
unauthenticated limit is 60 requests/hour per IP and brutal behind shared NAT.

**Revocation.** A version-ranged blocklist keyed on **publisher identity**, shipped as 4-byte hash
prefixes so a clean install generates no network traffic. Must ship *with* the loader, not after.

**Blast-radius limitation.** The things that remain possible at full trust: lock down `AppHostHooks`
before any loading; authenticate the LiveSync channel; keep add-ons entirely away from the CS2
automation surface (owner-directed scope boundary, and now also a security boundary); per-module entity
views so one add-on cannot corrupt another's reads; failure isolation and a circuit breaker that names
the culprit; and an add-on-attributed entry in the Output panel so misbehaviour is visible.

### 3.4 Scope ambiguity the owner should resolve

"UI pages and features" is treated in this document as **including the non-UI compute an add-on needs to
make its page useful** (a background analysis pass, an exporter, a data provider) on the reasoning that
a UI page with no compute behind it is inert. That assumption changes the contract surface materially
(background threads, long-running work, file export, and their failure and cancellation semantics), so
it is flagged rather than assumed silently. **If the intent is narrower (pages that render only
host-provided state) the contract stays much smaller and several roadmap items shrink.**

---

## 4. Rejected alternatives

Recorded so they are not re-litigated. Items marked **moot** were rejected by the scope narrowing rather
than on their merits; they are kept so the change is visible.

**Investing in the declarative/rules tier as the answer to third-party extensibility.** **Moot under the
narrowed scope, and this was Rev. 1's primary recommendation.** The argument was sound on its own terms:
Rules v2 is safe by construction, needs no trust decision, and works on every head. But it answers a
question the owner did not ask. Rulesets produce stats and highlights; they cannot produce a UI page.
Rules v2 hardening remains worth doing (§5, Phase 0) as maintenance of a shipping feature, not as an
extensibility strategy.

**Full trimming or NativeAOT.** Already rejected repo-wide (`build-and-packaging-plan.md:199-208`).
Restated because it is *load-bearing in our favour*: NativeAOT **cannot load managed assemblies at
runtime at all** (`Assembly.LoadFile` is on the documented unsupported list), so an AOT future would
foreclose the add-on system permanently. Trimming would produce the worst failure profile available: an
add-on calling a framework API the trimmer removed fails at run time, arbitrarily late, on whichever path
the user happens to hit.

**Out-of-process add-ons, WASM/WASI sandboxing, OS sandboxing, in-process managed sandboxing, embedded
VMs.** All rejected because none can return an Avalonia `Control` (§2.1, §2.6). Secondary costs recorded
in §2.6 so nobody re-opens them assuming the secondary costs were the only obstacle.

**Roslyn / `CSharpScript` as a scripting tier.** Full trust with none of the loader's structure, plus
tens of MB of Roslyn and a hard JIT requirement. If we are going to run arbitrary code we should load
assemblies and be honest about it.

**Capability manifests presented as enforcement.** Rejected as a *claim*; retained as disclosure plus
genuine enforcement on host-mediated operations only, with the two clearly labelled (§2.6).

**A bare "JSON index in GitHub + fetch from arbitrary GitHub repos" registry** (the Obsidian model).
Attractively cheap, but its failure modes are measurable: of 175 entries in Obsidian's removed-plugins
list, **~124 (71%) are supply-availability failures** (repository archived, deleted, account deleted,
developer banned, release missing) inherent to storing a *repo pointer* rather than an artifact. Their
index entry has no version, no URL, and no hash anywhere in the chain, and GitHub release tags are
mutable by default. Hence NuGet-as-artifact-store in §3.3.

**Custom NuGet `PackageType` as the add-on marker.** There is a server-side allowlist: `DotnetTool` and
`Template` resolve, custom types return zero, and unrecognised types have been reported to break VS's
package manager. Use `tags` plus the contract-package dependency instead.

**Curated marketplace with human security review.** Does not scale to a small team, and demonstrably
fails at large ones (§2.6).

**Letting add-ons reference CS2DemoKit.** Forbidden, and the firmest technical conclusion in the
document. `Directory.Build.targets:50-62` states the family *"shares no compatibility contract across
versions, so the floor range is a lie that surfaces as a runtime `MissingMethodException`."* If an add-on
ships its own copy, types load twice and either casts fail or discovery silently returns empty; if it
excludes it, the add-on binds to whatever the host shipped and gambles against a package line that
explicitly disclaims compatibility. The Abstractions assembly's Avalonia-only dependency set is what
makes a stable contract possible. **Enforce with a load-time reject**: roughly 15 lines with
`System.Reflection.Metadata`, converting an unbounded class of runtime crashes into one clear
install-time error.

**Add-ons on the Browser/WASM head.** Not possible (§1.5). Document add-ons as desktop-only rather than
leaving users to discover it.

---

## 5. Phased roadmap

**Phase 0: Standalone fixes. Do these regardless of every other decision here.**
Make `AppHostHooks` write-once or internal. Authenticate localhost:50051 with a per-session secret. Bump
YamlDotNet to ≥ 18.1.0 and add a test asserting `PreventUnknownTagsNodeTypeResolver` is in the chain.
Reword the leftover-restore copy to name the symptom, *"CS2 will not launch until this is restored"*,
and consider auto-restoring at startup with a notice (§1.6). Fix `RuleSetLocator`'s parent-directory walk
(97-107), which lets any `rules/` folder above the install capture the shipped tier.

**Phase 1: Make the existing contract honest.** Cheap, independently valuable, prerequisite to
describing the contract to anyone.
Enforce capabilities on the host-mediated operations via a per-module context wrapper: real, cheap, and
it makes shipped doc-comments true. Stop sharing the mutable `ReadOnlyEntityView` and pooled facade
across modules. Wrap `ViewFactory`/`Activate` in the failure isolation that already protects
`CreateTabs`, and attribute failures to the module by id. Decide what `ContractVersion` means and read
it. Close or gate `ICurrentDemoSource`. Correct the `IModuleContext` doc comment so it stops claiming to
be a guardrail.

**Phase 2: Ship the SDK without shipping a loader.** The highest-leverage phase, and low-risk because
nothing loads at runtime.
Flip `IsPackable` on the abstraction assembly (pack infrastructure already exists at
`Directory.Build.targets:19-65`; the "repo is private" comment there is now stale). Add
`Microsoft.CodeAnalysis.PublicApiAnalyzers` and `EnablePackageValidation` with a baseline so the contract
cannot break by accident, both cheap and consistent with the repo's `TreatWarningsAsErrors` house style.
Pay the pre-1.0 forward-compatibility debts while they are still free: seal what should be sealed, add
`Unknown = 0` to public enums, add an unload/`IDisposable` hook to `IWorkspaceModule`, and codify the
**default-interface-member pattern the contract already uses instinctively** (`MapName`, `LiveSyncHud`,
`DemoReset`, `GetEventTimeline` are all defaulted) as the written rule for growing interfaces. Publish a
test-double package: the app tests currently hand-roll **at least eight separate `FakeCtx :
IModuleContext` classes**, so this pays for itself immediately. Publish a `dotnet new` template carrying
the correct `ExcludeAssets="runtime"` incantation, eliminating the entire "my module loads but the cast
fails" bug class. Ship samples against `assets/tour/sample-de_nuke.dem`, an 11 MB structurally-complete
demo **already committed, already redistributed in every installer, and already privacy-vetted**
(`docs/tour-sample-demo.md`), making it a zero-cost fixture.

**Convert one first-party module to load from the package across an assembly boundary.** This is the
real test of the contract and it has never been run.

**Phase 3: The loader, with consent, provenance, and revocation shipped together.**
Manifest schema and a manifest-only discovery pass. Collectible ALC per add-on with explicit shared
types. `<config>/add-ons/` with the `ThemeRegistry` discovery discipline (deterministic order, skip
malformed, no shadowing of built-in ids). `MetadataLoadContext` pre-flight and the CS2DemoKit-reference
reject. Stage-and-apply-on-restart. Off-by-default restricted mode with the honest consent prompt from
§3.3. Minisign signatures and publisher identity. Prefix-based blocklist. `--add-on-dev-path` for authors
(VS Code's `--extensionDevelopmentPath` one-for-one; roughly an hour's work and about 80% of a dev mode).
**No remotely-hosted code and no self-updating add-ons**, as written policy from the first line.

**Phase 4: Distribution and ecosystem.** NuGet artifact store plus signed JSON catalog (§3.3), in-app
browse and install, automated and published checks rather than a human review queue, and a documented
compatibility policy.

---

## 6. Open questions requiring the owner's decision

Questions answered by the owner's scope correction have been removed rather than left to be
re-answered, specifically "do we want third-party code at all" (yes) and "is a richer declarative tier
the real goal" (no).

1. **Does "UI pages and features" include background compute** (analysis passes, exporters, data
   providers) or only pages rendering host-provided state? §3.4. This is the largest remaining
   uncertainty and it changes the contract surface materially.
2. **Are we willing to show a full-trust consent prompt that says an add-on can do anything the app can
   do?** If not, this system cannot ship honestly, because there is no way to make it safe and no honest
   way to imply that it is.
3. **Does signing the app become a prerequisite?** An unsigned host asking users to trust signed add-ons
   is a weak position.
4. **Is the contract version decoupled from the app version?** Recommended: yes, the app can go 0.7.1 →
   1.4.0 with the contract stable at 1.0.0. `IWorkspaceModule.ContractVersion` already anticipates this
   but nothing reads it.
5. **What is the compatibility promise across auto-updates?** Velopack updates silently, so an add-on
   compiled against contract 1.0 must have a defined and *visible* outcome when the host ships 1.1.
6. **Who owns review, and what is the throughput budget?** If the answer is "nobody", say so in the UI
   rather than implying curation that does not exist.
7. **Is `add-on` the right user-facing word**, given "plugin" is taken by the CSVG game plugin and
   "module" is the internal term?

---

## 7. Evidence gaps

- **The web-search budget was exhausted during this research**, and Rev. 2 added no new web research.
  Several claims sourced from the open web could not be independently re-verified. Everything cited from
  *this repository* and from `C:\dev\CS2DemoKit` **was verified first-hand**.
- **Not verified first-hand:** CS esports espionage incidents; Steam credential-theft specifics; Apple's
  App Sandbox signing requirement in Apple's own words; the current state of `wasmtime-dotnet` component
  support beyond the cited issue; the marketplace-incident and Obsidian removed-plugin figures in §2.6
  and §4.
- **Taken as authoritative from the owner, not independently verified:** that the CSVG plugin enforces
  `-insecure` at load and CS2 will not launch without it. A string scan of the shipped native binaries
  did not find it, but that is inconclusive; a stripped binary or a runtime-constructed string would
  both explain it. §1.6's consequence (post-crash, CS2 will not launch until restored) follows from that
  statement combined with `InstallRecovery.cs:14-19`; the flyout copy and gating were verified directly.
- **Needs a spike before we promise add-on unload:** whether `AvaloniaProperty.Register`'s type-keyed
  global registry permanently roots an add-on's `Type`. If it does, styled properties and unload are
  mutually exclusive. Extrapolated from general .NET unloadability rules, not verified against Avalonia.
- **Legal opinion, not settled law:** the GPL/dynamic-linking question. The practical position is
  comfortable: the host is MIT (`LICENSE`, `README.md:103`), so there is no copyleft to propagate and no
  reason to restrict add-on licences; we never distribute add-ons; and the thin MIT-licensed contract
  assembly is the .NET equivalent of a linking exception achieved by construction. Keep it thin, never
  bundle third-party add-ons in the installer, write the policy down.
- **Assertion deliberately avoided:** the widely-repeated paraphrase that "the .NET Core threat model
  assumes code in the same process is fully trusted" is not used, because it could not be attributed to
  Microsoft. The sourced Kotas and Matoušek quotes in §2.2 make the same point and are stronger.

---

## 8. Revision record

**Rev. 2 (2026-08-20)**: two owner corrections, both structural.

**Correction 1: the VAC finding was wrong and has been removed.** Rev. 1 opened with a prominent
"Finding 0" asserting a live user-safety gap: that live-sync's VAC safety depended on CSVG passing
`-insecure`, that this repo never verifies it, and that a post-crash leftover could load a native plugin
into a VAC-secured session. **The reasoning was wrong at its root.** We grepped *this* repo for
`-insecure` and concluded the property was unenforced, but enforcement was never supposed to live here:
per the owner, the CSVG plugin itself checks for `-insecure` at load and refuses to let CS2 start
without it: fail-closed, inside the dangerous component, exactly where Rev. 1 praised HLAE for putting
it. The banner, the threat-table row, the roadmap items, and the open question have all been deleted.

What survives is smaller and **inverted in severity**: because the plugin refuses to run without
`-insecure`, a post-crash leftover means the user's *normal* CS2 launch is blocked until restored. That
is user-facing breakage, not a ban risk. It now appears as a short subsection (§1.6) recommending clearer
copy and possible auto-restore, sized to its actual importance.

The design principle Rev. 1 derived from it, *make the dangerous state structurally unreachable rather
than documenting it*, is **kept, but re-derived**, since its original justification evaporated. It now
rests on things that are true: the CS2DemoKit load-time reject (§4) rather than documenting a version
rule, `AppHostHooks` made write-once rather than documented as off-limits, and stage-and-apply-on-restart
rather than warning about locked DLLs. The CSVG plugin's own `-insecure` check is, in fact, a good
example of the principle rather than a counter-example.

**Correction 2: the scope narrowed, which invalidated Rev. 1's primary recommendation.** The owner
scoped this to third-party **UI pages and accompanying features**, explicitly excluding live-sync
extension and a richer declarative tier. Rev. 1 recommended investing in Rules v2 and shipping the SDK
without a loader. **The first half answered a question that was not asked**: rulesets cannot produce a UI
page, and is now recorded as moot in §4 rather than quietly dropped. The second half survives as Phase 2.

Consequences worked through in this revision: the `ViewFactory → Control` observation was promoted from a
supporting point (Rev. 1 §3.3) to **the central finding** (§2.1), because it means full trust is a
property of the requirement rather than a choice; the isolation survey was reframed from "our options" to
"why there are none for this seam" (§2.6) and moved into rejected-alternatives territory; the three-tier
structure **collapsed to one tier**, since the narrowed scope does not support more; the security centre
of gravity moved to consent, provenance, distribution integrity, revocation, and blast radius (§3.3); and
§5 and §6 were rewritten, with two now-answered open questions removed.

**Kept from Rev. 1:** the §1 current-state inventory, the threat model minus the VAC row, the supply-chain
analysis, both incidental bugs, the rejected alternatives, and the evidence gaps.

[cas]: https://learn.microsoft.com/en-us/previous-versions/dotnet/framework/misc/code-access-security
[r4108]: https://github.com/dotnet/runtime/issues/4108
[r10830]: https://github.com/dotnet/roslyn/issues/10830
[yaml1109]: https://github.com/aaubry/YamlDotNet/issues/1109

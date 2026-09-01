# Build, Packaging & Distribution Plan

Rev. 3 decisions implemented for the in-repo tiers (2026-07-21). Rev. 3 (owner direction):
**drop the portable framework-dependent tier for now**: ship only per-platform self-contained builds.
Rev. 2 direction stands: Linux is a first-party target; the CSVG native package must ship mock **and**
real servers for all three platforms (the earlier review examined a *partial* package); signed+notarized
is the intended end state but deferred.

> **Implementation status (2026-07-21):** §8 steps 2–5 landed. `scripts/publish.sh` is the generalized
> self-contained per-RID publisher (win-x64/osx-arm64/linux-x64). CSVG natives need **no manual copy**:
> `dotnet publish -r <rid>` already flattens the target RID's `runtimes/<rid>/native/*` next to the exe
> (a CSVG probe path), for whatever `Cs2VideoGenerator.Core` version is referenced and cross-RID
> (verified: win-x64 natives land when publishing on macOS, both self-contained and framework-dependent).
> The script only **verifies** they landed, version-independently: it reads the version NuGet actually
> resolved (`project.assets.json`), finds that package in the global-packages cache, and requires every
> native it carries for the RID to appear in the output. Self-maintaining: a platform whose pack gains
> natives (e.g. linux `server.so`) starts requiring them automatically; a repack that drops one fails;
> a RID with no natives ships without live-sync (no failure). `DV_PUBLISH_ALLOW_NO_CSVG_NATIVES=1` still
> forces a parser-only bundle. `Cs2VideoGenerator.Core` restores from **nuget.org** like every other
> dependency: no extra feed, no credentials.
> `scripts/pack-velopack.sh` wraps the publish into Velopack installers (`Velopack`
> 1.2.0 runtime ref + `VelopackApp.Build().Run()` first in `Program.Main`; `vpk`/`nbgv` pinned in
> `.config/dotnet-tools.json`). CI: `.github/workflows/release.yml` (tri-OS vpk matrix) +
> `ci.yml` (Desktop build-check). **Verified locally:** the SDK's cross-RID native flattening +
> version-independent guard (against the `rc.28` pack); earlier osx-arm64 self-contained bundle launches;
> osx-arm64 Velopack `.app`/`.pkg` + delta feed produced (unsigned). **Still owner-gated:** authenticating
> the private feed (+ wiring those creds as CI secrets) and §6 (signing/notarization). The full-platform
> natives are now expected to ship in the `1.0.0` feed release (§8 step 1).

---

## The recommendation

**Ship per-platform self-contained builds** for **win-x64, osx-arm64, and linux-x64** (all three
first-party). Each bundles the .NET 10 + ASP.NET Core runtimes + that platform's natives → **no runtime
prerequisite** on the target, and auto-updating. Packaged with **[Velopack](https://velopack.io/)**
(Windows `Setup.exe`, notarized macOS `.app`/`.dmg`, Linux AppImage).

> A single **portable framework-dependent** bundle (all-platform natives, one cross-OS artifact) is
> technically viable and was verified (§4), but is **explicitly out of scope for now**; revisit only if
> a "user already has the .NET runtime, wants one bundle" need appears.

Other verdicts (unchanged, and firm):
- **No NativeAOT.** Multiple hard blockers ship into the app (§1).
- **No trimming** (beyond, at most, `copyused` + `InvariantGlobalization` later): Avalonia/ASP.NET/
  YamlDotNet break under it; small payoff (§3).
- **Linux → AppImage, not Flatpak**, for the live-sync build (§5).
- **The one real cross-cutting cost is building the CSVG native C++ libraries for all three platforms**
  (§2), a task in the sibling CSVG repo, not a limitation of this app.

Signing/notarization is the intended end state but **deferred**; ship unsigned for now (§6). The
decisions still genuinely open are in §8.

---

## 1. NativeAOT. Verdict: no, for the shippable app

NativeAOT needs no runtime code generation and a trim-clean reflection surface. The Desktop head
violates both, via components directly referenced by the app:

| Blocker | Where | Why it kills whole-app AOT |
|---|---|---|
| Runtime `Expression…Compile()` | `Analysis/Building/ExpressionCompiler.cs` (167, 173, 189, …) | The rule-engine compiles predicates at runtime. AOT has no Reflection.Emit → silently falls back to the LINQ **interpreter** (slower). |
| `MakeGenericType` / `Activator.CreateInstance` over **value types** | `Analysis/Building/RuleChainBuilder.cs`, `EntityChangeScanner.cs`, `GenericEntityFieldProviders.cs:182` | Closing generics over value types not statically reachable throws under AOT. |
| **MSAGL** (graph rendering) | `Visualization/` → App | No AOT/trim support; heritage reflection. A hard blocker alone. |
| ASP.NET Core + Kestrel + `Cs2VideoGenerator.Core` gRPC host | `LiveSync/CsvgWebHost.cs` | Not AOT-targeted; brings the reflection-heavy hosting stack. |

**Myth corrected:** gRPC *server* hosting itself **is** AOT-supported on modern .NET
([ASP.NET Core AOT table](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot): "gRPC
= Fully Supported"). Only the optional `Server.Reflection` add-on (unused) warns. Our blockers are
MSAGL + our own `ExpressionCompiler`, not gRPC.

Standalone-AOT is feasible only for the pure leaf libs (`Parser`, `EntityTracking`, `Entities`,
`Visibility`, `Analysis.Rules`), which we don't ship standalone, so ROI ≈ 0.

## 2. CSVG native C++ libraries: the real cross-cutting cost (a build task, not a limitation)

The app loads `Cs2VideoGenerator.Core`'s native binaries per RID (mock + real server). Coverage in the
referenced pack (`0.9.0`, audited against its `runtimes/` tree):

| Platform (RID) | mock server | real server | Notes |
|---|---|---|---|
| Windows (win-x64) | `mock_server.exe` | `server.dll` | complete |
| Linux (linux-x64) | `mock_server` | `server.so` | complete |
| macOS (osx-arm64) | `mock_server` | *(to build)* | mock only; live sync is limited here |

> Exact filenames/extensions per RID are a CSVG-repo detail; confirm them against CSVG's
> `NativeAssetProvider` probe (see the §4 spike) before finalizing the pack.

**This is the single largest recurring maintenance item of the whole plan**, larger than the packaging
tool. Each platform's natives are built + verified in the sibling CSVG repo and the package republished.
The open item is the macOS real server: that cross-compile has a masked SDKROOT/vcpkg issue, and it is
what gates first-party macOS parity.

## 3. Self-contained & trimming

- **Self-contained (bundles the .NET 10 + ASP.NET Core runtimes):** yes,
  matches "only ship what isn't reasonably on the platform default list." ~58 MB unzipped / ~25 MB
  zipped, untrimmed; the runtime + SkiaSharp (~10 MB) dominate.
- **Trimming: skip it.** Avalonia doesn't support `TrimMode=link`/full ("crashes inconsistently"; only
  `copyused` supported); ASP.NET Core defaults `TrimMode=full`; YamlDotNet (ships via the Rule
  Workbench) + our STJ + `Configuration.Binder` reflection all warn/break. Payoff over the runtime floor
  is small. If size pressure gets real later: `TrimMode=copyused` + `InvariantGlobalization` (drops ICU,
  ~30 MB), never full/AOT.

## 4. Self-contained is per-RID (and the portable option we're deferring)

**Self-contained is inherently per-RID.** Each publish (`-r win-x64` / `osx-arm64` / `linux-x64`
`--self-contained`) bundles the runtime + that platform's natives into one artifact for one OS+arch.
That's the tier we ship. A single *self-contained* archive that runs on all three OSes is impossible.

**Deferred (out of scope, but proven possible):** a **portable framework-dependent** bundle. For the
record, so we don't re-litigate feasibility later, `dotnet publish -c Release` (no `-r`) was verified
to lay down the full `runtimes/<rid>/native/` tree for every RID the packages carry (Skia/HarfBuzz for
all RIDs *and* the CSVG natives present in the package), and .NET's RID-based native resolution selects
the correct set at runtime. So one cross-OS bundle is achievable *if* we ever want it. Its trade-offs
(why it's not the default): it's framework-dependent (needs .NET 10 **+** ASP.NET Core 10 runtimes
installed, no coreclr bundled), the apphost is per-OS (launch via `dotnet app.dll` or ship per-OS
launchers), and it hinges on CSVG's `NativeAssetProvider` being RID-parameterized rather than
`win-x64`-hardcoded. None of that matters while it's out of scope; noted for whenever it comes back.

## 5. Packaging format

**Per-platform self-contained builds → [Velopack](https://docs.velopack.io/)** (maintained Squirrel
successor): one `vpk` CLI → Windows `Setup.exe`, notarized macOS `.app`/`.dmg`, Linux **AppImage**, with
real **delta background auto-updates** + apply-on-restart, and it **notarizes macOS for you** (when we
enable signing; §6).

- **Windows** → `Setup.exe` (per-user `%LocalAppData%`, no elevation).
- **macOS** → notarized `.app` (+ `.dmg`).
- **Linux (first-party)** → **AppImage.** *Not Flatpak:* live-sync launches the game and patches the
  user's Steam install (`LiveSync/InstallRecovery.cs` patches `gameinfo.gi`; CSVG copies plugin files +
  launches CS2 + opens a Kestrel loopback port). Flatpak's sandbox fights all of that (broad
  `--filesystem=host`, `flatpak-spawn --host` ≈ sandbox escape, and `~/.var/app` Steam libraries need a
  separate grant). AppImage is unsandboxed and sidesteps it. `.deb`/`.rpm` are optional later.

**Velopack caveats:** `current/` is replaced on every update, but deltas
content-diff between releases, so the 22 MB of in-package baked map assets only cost on **first install**
(unchanged assets add ~nothing to updates). → **ship assets in-package; don't build a download-on-demand
cache.** Windows file-locks: the CS2 process lives in the user's Steam dir, not our tree, so the swap is
safe. CI is DIY (Velopack's GH Actions sample is Windows-only; we build the tri-OS matrix). Spike:
confirm AppImage bundles the CSVG `.so`s cleanly and macOS notarization passes with the native libs
(each native lib must be hardened-runtime-signed before the bundle is notarized+stapled).

**MSIX:** Windows-only, only full-trust (never AppContainer: it'd break live-sync's file access); worth
it only if we later target the Microsoft Store.

## 6. Code signing & notarization: intended end state, deferred

Per owner: **ship signed + notarized eventually** (the right UX: no SmartScreen/Gatekeeper scare
screens), but **defer until we commit to a fully-shipped/open product.** Ship **unsigned** for now
(users right-click→Open on macOS, "Run anyway" on Windows), fine for the current
early/enthusiast audience.

When we turn it on, budget the recurring cost:
- **macOS:** Apple Developer Program ($99/yr) + Developer-ID certs + a `notarytool` credential in CI.
  Keep the App Sandbox **off** (separate opt-in) to preserve file access + process launch. Velopack
  drives the notarization.
- **Windows:** an OV/EV code-signing cert (annual) to clear SmartScreen.

Design so this is a later config flip, not a re-architecture: keep signing identities/entitlements as CI
secrets + build parameters from day one (empty = unsigned).

## 7. "Core vs Full": not needed for Linux anymore

Linux is now first-party **Full** (once CSVG Linux natives exist, §2), so the earlier "Core is the only
way to reach Linux" argument is **moot**. A Core build (no LiveSync → no CSVG native dependency,
Flatpak-viable, smaller) is now only interesting as a *future optional lightweight/analysis-only
edition*, not required by the platform plan. Don't build it unless that edition becomes a goal; it's a
second config to maintain against the "least maintenance" priority.

## 8. Proposed sequence

1. **CSVG native completeness (§2): the gating prerequisite. Outstanding: the osx-arm64 real server.**
   Build + verify it in the CSVG repo, then publish `Cs2VideoGenerator.Core` to nuget.org and bump the
   version in `Directory.Packages.props`. The version-independent guard in `publish.sh` then requires
   whatever natives that pack carries per RID; no script change as coverage grows.
2. **Spike: self-contained per-RID actually runs. Done (osx-arm64 verified; win/linux publish-verified).**
   `scripts/publish.sh <rid>` lays out Skia + CSVG natives + baked assets self-contained. osx-arm64 bundle
   launches; win-x64/linux-x64 publishes succeed (launch on those OSes covered by the CI matrix).
3. **Generalize the publish script → `scripts/publish.sh`. Done.** Self-contained by default,
   parameterized RID, version-independent CSVG-native verify guard (natives are placed by the SDK) +
   baked-asset copy. `publish-win-x64.sh` is now a back-compat shim (framework-dependent win-x64).
4. **Wrap with Velopack → `scripts/pack-velopack.sh`. Done (delta feed emitted; full v1→v2 update
   cycle still to exercise on a real install).** `vpk pack` per RID; `Velopack` runtime ref +
   `VelopackApp.Build().Run()` bootstrap; signing is an env-flip (§6).
5. **CI matrix: `.github/workflows/release.yml`. Done (files in place; first run pending push).**
   `windows-latest` + `macos-14` (arm64) + `ubuntu-latest`; `ci.yml` adds a Desktop build-check. Signing
   wired via repo secrets later (§6).

## Decisions you own

1. **RID breadth beyond the three first-party (win-x64, osx-arm64, linux-x64)?** e.g. win-arm64,
   osx-x64, linux-arm64: each needs CSVG + (already-present) Skia natives. Default: the three, add
   others on demand.
2. **Auto-update channel/hosting for Velopack**: GitHub Releases (simplest, free) vs S3/other. Affects
   the delta-update setup.

## Explicitly rejected (don't revisit without new information)

- Full-app **NativeAOT** (MSAGL + `ExpressionCompiler` + ASP.NET stack).
- **Full trimming** (Avalonia + ASP.NET + YamlDotNet break; small payoff).
- **Flatpak** for the Full/live-sync build (sandbox fights the game-launch + install-patch feature),
  AppImage instead.
- **MSIX AppContainer** (would break live-sync's file access).
- **The portable framework-dependent cross-OS bundle**, deferred by owner decision (§4); revisit only
  on a concrete "user has the runtime, wants one bundle" need. (A single *self-contained* cross-OS
  bundle stays impossible regardless: self-contained is always per-RID.)

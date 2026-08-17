#!/bin/bash
# Publishes a per-platform SELF-CONTAINED Desktop bundle (the shipping tier per
# docs/distribution/build-and-packaging-plan.md §3/§4).
#
#   ./scripts/publish.sh [RID] [--framework-dependent]
#
#   RID                    win-x64 | osx-arm64 | linux-x64  (default: this host's RID)
#   --framework-dependent  publish WITHOUT the bundled runtime (the old off-machine-test
#                          tier — target then needs .NET 10 + ASP.NET Core 10 installed).
#                          Default is self-contained: the bundle carries both runtimes, so
#                          the target has NO runtime prerequisite.
#
# Produces artifacts/publish/DemoViewer.NET-<RID>/ and a versioned zip beside it.
#
# Beyond `dotnet publish`, the bundle needs two asset families the SDK doesn't handle the same way:
#   1. cs2-assets/baked/<map>/   — pre-baked radar/floor/collision bundles (gitignored dev
#      cache). MapAssetLoader + CollisionAssetLocator walk up from the exe dir, and the
#      FIRST probe is <exeDir>/cs2-assets/baked/<map>/, so copying the cache next to the
#      binary ships 2D Playback radar art + the 3D visibility engine's collision.tris.
#      The SDK knows nothing about these — this script copies them explicitly.
#   2. CSVG native assets — Cs2VideoGenerator.Core's NativeAssetProvider probes
#      <exeDir>/runtimes/<rid>/native/{<real>,<mock>} then <exeDir>. These are ordinary NuGet
#      native runtime assets (runtimes/<rid>/native/* in the pack), so a RID-targeted
#      `dotnet publish -r <rid>` ALREADY flattens the target RID's copies next to the exe —
#      version-independently and cross-RID (verified: win-x64 natives land when publishing on
#      macOS; both self-contained and framework-dependent). <exeDir> is a CSVG probe path, so no
#      manual copy is needed. This script only VERIFIES the natives landed (best-effort, never
#      fatal on its own): it reads the referenced version from Directory.Packages.props, finds that
#      package in the global-packages cache, and checks every native it carries for this RID
#      appears in the output — WARNING if any are missing. Self-maintaining as the pack gains a
#      platform's natives. To ship a platform whose natives aren't built yet, build them FIRST in
#      the Cs2VideoGenerator repo and republish Core (docs/distribution §2). Set
#      DV_PUBLISH_STRICT_NATIVES=1 to turn a genuine missing-native into a hard failure.

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# --- args -------------------------------------------------------------------
host_rid() {
    local os arch
    case "$(uname -s)" in
        Darwin) os=osx ;;
        Linux)  os=linux ;;
        *)      os=win ;;
    esac
    case "$(uname -m)" in
        arm64|aarch64) arch=arm64 ;;
        *)             arch=x64 ;;
    esac
    echo "$os-$arch"
}

RID=""
SELF_CONTAINED=1
for arg in "$@"; do
    case "$arg" in
        --framework-dependent) SELF_CONTAINED=0 ;;
        --self-contained)      SELF_CONTAINED=1 ;;
        -*) echo "unknown flag: $arg" >&2; exit 2 ;;
        *)  RID="$arg" ;;
    esac
done
[ -n "$RID" ] || RID="$(host_rid)"

case "$RID" in
    win-x64|osx-arm64|linux-x64) ;;
    *) echo "unsupported RID: $RID (first-party: win-x64, osx-arm64, linux-x64)" >&2; exit 2 ;;
esac

OUT=artifacts/publish/DemoViewer.NET-$RID

if [ "$SELF_CONTAINED" = "1" ]; then
    SC_FLAG=--self-contained
    TIER=self-contained
else
    SC_FLAG=--no-self-contained
    TIER=framework-dependent
fi

echo "publishing DemoViewer.NET Desktop: RID=$RID tier=$TIER"
dotnet publish src/App/DemoViewer.NET.Desktop -c Release -r "$RID" $SC_FLAG -o "$OUT"

# 1. Baked map-asset bundles (radar + collision.tris). These are committed under assets/ (repo root),
#    so they ship in every build — including CI, where the gitignored cs2-assets/ dev cache is absent.
#    Copied to <exeDir>/assets/, which MapAssetLoader + CollisionAssetLocator probe first. Use cp -R,
#    NOT rsync — git-bash on the Windows runner has no rsync (would be a command-not-found failure).
if [ -d assets ] && [ -n "$(ls -A assets 2>/dev/null)" ]; then
    rm -rf "$OUT/assets"
    cp -R assets "$OUT/assets"
    echo "baked bundles: $(ls assets | tr '\n' ' ')"
else
    echo "WARNING: assets/ not found or empty — bundle ships without radar/collision assets" >&2
fi

# 2. CSVG natives — the SDK already flattened the target RID's runtimes/<rid>/native/* next to the
#    exe during publish (a CSVG probe path), for whatever Cs2VideoGenerator.Core version is
#    referenced. We don't copy them; we VERIFY they landed:
#      a. read the referenced version from Directory.Packages.props (repo root — layout-independent;
#         obj/ moves under artifacts output, so project.assets.json is NOT a reliable path),
#      b. locate that package in the global-packages cache,
#      c. check every native it carries for this RID appears in the output.
#    This is a best-effort SAFETY NET, not the mechanism (the SDK does the placement). It NEVER
#    aborts the publish: the whole block runs under `set +e`, and a missing native only WARNS —
#    unless DV_PUBLISH_STRICT_NATIVES=1, which turns a genuine miss into a hard failure (flip this
#    on in CI once a green run's native list is trusted). Self-maintaining: as the pack gains a
#    platform's natives they show up in the list automatically.
set +e   # ---- best-effort native verification; must never fail the publish on its own bugs ----
CSVG_PKG_ID=cs2videogenerator.core   # global-packages dir names are lowercased
gp=$(dotnet nuget locals global-packages --list 2>/dev/null | sed 's/^[^:]*:[[:space:]]*//' | tr -d '\r')
[ -n "$gp" ] || gp="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
gp="${gp//\\//}"   # Windows git-bash returns C:\...\ — normalize to / so find / [ -d ] behave
csvg_ver=$(sed -n 's/.*Cs2VideoGenerator\.Core"[^>]*Version="\([^"]*\)".*/\1/p' Directory.Packages.props)
csvg_ver="${csvg_ver%%$'\n'*}"   # first match, without `| head` (avoids SIGPIPE-under-pipefail)
csvg_native_dir="$gp/$CSVG_PKG_ID/$csvg_ver/runtimes/$RID/native"

# Diagnostic: show what actually landed (helps confirm the real per-RID native layout in CI logs).
echo "CSVG native check: version='$csvg_ver' rid=$RID"
echo "  pack native dir: $csvg_native_dir"
echo "  in bundle root:  $(ls "$OUT" 2>/dev/null | grep -iE '^(mock_)?server(\.|$)' | tr '\n' ' ')"
[ -d "$OUT/runtimes/$RID/native" ] && echo "  in runtimes dir: $(ls "$OUT/runtimes/$RID/native" 2>/dev/null | tr '\n' ' ')"

# have_native: search the whole bundle (root, runtimes/<rid>/native/, and any subdir the SDK may
# preserve) so real/+mock/ nesting doesn't cause false misses.
have_native() { find "$OUT" -name "$1" -type f -print -quit 2>/dev/null | grep -q .; }

missing=""
if [ -z "$csvg_ver" ] || [ ! -d "$csvg_native_dir" ]; then
    echo "WARNING: couldn't resolve Cs2VideoGenerator.Core natives for $RID to verify" \
         "(version='${csvg_ver:-?}', dir missing) — skipping native check." >&2
else
    expected=$(find "$csvg_native_dir" -type f 2>/dev/null | sed 's#.*/##' | sort -u)
    if [ -z "$expected" ]; then
        echo "NOTE: Cs2VideoGenerator.Core $csvg_ver carries no natives for $RID — bundle ships" \
             "without live-sync for this platform (see docs/distribution/build-and-packaging-plan.md)." >&2
    else
        # while-read, not `for f in $expected` — robust to whitespace and to shells that don't
        # word-split unquoted expansions.
        while IFS= read -r f; do
            [ -n "$f" ] || continue
            have_native "$f" || missing="$missing $f"
        done <<< "$expected"
        if [ -n "$missing" ]; then
            echo "WARNING: CSVG native(s) the pack carries for $RID are missing from output:$missing" >&2
            echo "         Expected from Cs2VideoGenerator.Core $csvg_ver ($csvg_native_dir)." >&2
            echo "         Live-sync will be limited on this platform." >&2
        else
            echo "CSVG natives ($csvg_ver, $RID): $(echo $expected | tr '\n' ' ')— present"
        fi
    fi
fi
set -e   # ---- end best-effort block; restore errexit before the (fatal) steps below ----

# Strict mode (opt-in): a genuine missing native is a hard error. Off by default so the guard's
# own uncertainty can't red-X a release; flip DV_PUBLISH_STRICT_NATIVES=1 in CI once trusted.
if [ -n "$missing" ] && [ "${DV_PUBLISH_STRICT_NATIVES:-0}" = "1" ]; then
    echo "ERROR: missing CSVG native(s) for $RID:$missing (DV_PUBLISH_STRICT_NATIVES=1)." >&2
    exit 1
fi

echo "publish: $OUT ($TIER, $RID)"

# Convenience zip of the publish dir (for standalone off-machine testing). This is NOT what the
# release matrix ships — vpk packs from $OUT directly — so it must NEVER be fatal: the archiver
# varies by OS (macOS ditto, Linux `zip`, Windows git-bash has NEITHER but has PowerShell), and a
# missing one shouldn't fail the whole publish. Best-effort, warn-and-continue on failure.
SHA=$(git rev-parse --short HEAD)
csvg_tag=$(printf %s "${csvg_ver:-unknown}" | tr -c 'A-Za-z0-9._-' '-')
ZIP=artifacts/publish/DemoViewer.NET-$RID-$SHA-csvg-$csvg_tag.zip
ZIP_ABS="$PWD/$ZIP"
rm -f "$ZIP"
make_zip() {
    if command -v ditto >/dev/null 2>&1; then
        ditto -c -k --keepParent "$OUT" "$ZIP"                                   # macOS: keeps symlinks
    elif command -v zip >/dev/null 2>&1; then
        ( cd "$(dirname "$OUT")" && zip -qr -X "$ZIP_ABS" "$(basename "$OUT")" ) # Linux
    elif command -v pwsh >/dev/null 2>&1 || command -v powershell >/dev/null 2>&1; then
        local ps; ps=$(command -v pwsh 2>/dev/null || command -v powershell)     # Windows
        "$ps" -NoProfile -NonInteractive -Command \
            "Compress-Archive -Force -Path '$OUT' -DestinationPath '$ZIP'"
    else
        return 1
    fi
}
if make_zip; then
    echo "zip:     $ZIP ($(du -h "$ZIP" 2>/dev/null | cut -f1 | tr -d ' '))"
else
    echo "NOTE: no archiver available to build $ZIP — skipped (convenience only; $OUT is complete)" >&2
fi

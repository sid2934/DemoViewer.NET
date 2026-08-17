#!/bin/bash
# Wraps a self-contained publish into a Velopack install/auto-update package
# (docs/distribution/build-and-packaging-plan.md §5):
#   Windows → Setup.exe · macOS → .app/.dmg · Linux → AppImage, each with delta updates.
#
#   ./scripts/pack-velopack.sh [RID]
#
#   RID   win-x64 | osx-arm64 | linux-x64  (default: this host's RID)
#
# vpk builds the installer for the HOST OS only — you cannot build a Windows Setup.exe on
# macOS. So run this on each platform (that's why CI is a tri-OS matrix). The RID
# must match the host OS family; a mismatch is rejected up front.
#
# Signing/notarization is DEFERRED (§6): unset identities = unsigned. Flip it on later via
# these env vars (empty = unsigned, no re-architecture):
#   DV_SIGN_APP_IDENTITY      subject name of the app-signing cert     (--signAppIdentity)
#   DV_SIGN_INSTALL_IDENTITY  subject name of the installer cert       (--signInstallIdentity)
#   DV_SIGN_ENTITLEMENTS      entitlements plist for hardened runtime  (--signEntitlements)
#   DV_NOTARY_PROFILE         notarytool credential profile name       (--notaryProfile)
#   DV_VPK_EXTRA_ARGS         escape hatch for anything not modeled (e.g. Windows --signParams)
#
# Delta updates: vpk diffs against the *.nupkg files already in --outputDir, so keep a
# release dir warm (download the previous channel release into it in CI) to emit deltas.

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

PACK_ID="DemoViewer.NET"
PACK_TITLE="DemoViewer.NET"
PACK_AUTHORS="DemoViewer.NET"

host_rid() {
    local os arch
    case "$(uname -s)" in
        Darwin) os=osx ;; Linux) os=linux ;; *) os=win ;;
    esac
    case "$(uname -m)" in
        arm64|aarch64) arch=arm64 ;; *) arch=x64 ;;
    esac
    echo "$os-$arch"
}

RID="${1:-$(host_rid)}"
HOST_OS="$(uname -s)"
case "$RID" in
    win-x64)   MAIN_EXE="DemoViewer.NET.Desktop.exe"; NEED_OS="MINGW|MSYS|CYGWIN|Windows" ;;
    osx-arm64) MAIN_EXE="DemoViewer.NET.Desktop";     NEED_OS="Darwin" ;;
    linux-x64) MAIN_EXE="DemoViewer.NET.Desktop";     NEED_OS="Linux" ;;
    *) echo "unsupported RID: $RID (first-party: win-x64, osx-arm64, linux-x64)" >&2; exit 2 ;;
esac
if ! echo "$HOST_OS" | grep -qE "$NEED_OS"; then
    echo "ERROR: $RID installers must be built on their own OS ($NEED_OS); host is $HOST_OS." >&2
    echo "       vpk cannot cross-build — run this on a $RID host (or in the CI matrix)." >&2
    exit 2
fi

OUT="artifacts/publish/DemoViewer.NET-$RID"
REL="artifacts/velopack/$RID"
VERSION="$(dotnet nbgv get-version -v NuGetPackageVersion)"

# Tag/version agreement guard. version.json is the single source of the stamped version — nbgv
# does NOT read it from the tag — so tagging v0.6.0 while version.json still says 0.5.1 silently
# ships 0.5.1 artifacts under a 0.6.0 release. That was always true, but it used to be visible:
# a tag build produced "0.5.1-g<sha>", which looked wrong at a glance. Now that version tags are
# a publicReleaseRefSpec match the output is a clean "0.5.1", which reads as legitimate. Same
# mistake, quieter failure — so fail loudly here instead.
TAG_REF="${GITHUB_REF:-}"
case "$TAG_REF" in
    refs/tags/v*)
        TAG_VER="${TAG_REF#refs/tags/v}"
        if [ "$TAG_VER" != "$VERSION" ]; then
            echo "ERROR: tag v$TAG_VER does not match the stamped version $VERSION." >&2
            echo "       version.json drives the artifact version; bump it to $TAG_VER, or" >&2
            echo "       re-tag to v$VERSION. (Ship-plan step 16 is the bump step.)" >&2
            exit 2
        fi
        echo "tag/version agreement: v$TAG_VER == $VERSION"
        ;;
esac

# 1. Self-contained publish (+ asset/native layout + guards).
./scripts/publish.sh "$RID"

# 1b. Delta seeding (opt-in: DV_DELTA_SEED=1, set by release.yml). vpk generates a delta whenever a
#     previous release's .nupkg is already sitting in --outputDir, so we fetch the current published
#     release into $REL before packing. Without this the dir starts empty every CI run and every
#     release ships full-only — which is what v0.5.0 and v0.5.1 did.
#
#     NOTE ON CHANNEL: deliberately NOT passed to either command. vpk defaults the channel from the
#     host OS for BOTH download and pack, so they agree by construction. Passing an explicit channel
#     here would mean maintaining a RID→channel mapping whose only failure mode is silently diffing
#     against ANOTHER platform's release — the exact "delta against the wrong base" hazard worth
#     designing out rather than testing for.
#
#     --pre true because release.yml publishes with --pre true; the latest STABLE would be nothing.
#
#     Never fatal. No previous release, no network, a rate-limited token: all mean "no delta base",
#     and a full-only release is a correct release. Failing the build here would turn a size
#     optimisation into an outage.
if [ "${DV_DELTA_SEED:-0}" = "1" ]; then
    echo "delta seed: fetching current release into $REL"
    mkdir -p "$REL"
    if dotnet vpk download github \
        --outputDir "$REL" \
        --repoUrl "${DV_REPO_URL:-https://github.com/sid2934/DemoViewer.NET}" \
        --pre true \
        ${DV_GITHUB_TOKEN:+--token "$DV_GITHUB_TOKEN"}; then
        echo "delta seed: base packages now in $REL:"
        ls -la "$REL" | grep -i nupkg || echo "  (none — first release on this channel; full-only is expected)"
    else
        echo "delta seed: download failed or no prior release — packing FULL-ONLY (not an error)" >&2
    fi
fi

# 2. Pack. Optional flags are appended only when their inputs exist/are set.
mkdir -p "$REL"
args=(
    --packId "$PACK_ID"
    --packTitle "$PACK_TITLE"
    --packAuthors "$PACK_AUTHORS"
    --packVersion "$VERSION"
    --packDir "$OUT"
    --mainExe "$MAIN_EXE"
    --runtime "$RID"
    --outputDir "$REL"
)
[ -n "${DV_SIGN_APP_IDENTITY:-}" ]     && args+=(--signAppIdentity     "$DV_SIGN_APP_IDENTITY")
[ -n "${DV_SIGN_INSTALL_IDENTITY:-}" ] && args+=(--signInstallIdentity "$DV_SIGN_INSTALL_IDENTITY")
[ -n "${DV_SIGN_ENTITLEMENTS:-}" ]     && args+=(--signEntitlements    "$DV_SIGN_ENTITLEMENTS")
[ -n "${DV_NOTARY_PROFILE:-}" ]        && args+=(--notaryProfile       "$DV_NOTARY_PROFILE")
# shellcheck disable=SC2206
[ -n "${DV_VPK_EXTRA_ARGS:-}" ] && args+=($DV_VPK_EXTRA_ARGS)

echo "vpk pack: id=$PACK_ID version=$VERSION rid=$RID -> $REL"
dotnet vpk pack "${args[@]}"

echo "velopack packages:"
ls -la "$REL"

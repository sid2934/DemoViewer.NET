#!/usr/bin/env bash
# Packages one first-party extension for release (docs/architecture/strat-book-plugin.md §7.11):
# builds it, stages exactly what the extension project itself produces, prints a compatibility report,
# signs and verifies the staged directory, zips it deterministically, and emits the feed entry fragment
# the release workflow merges into the rolling extensions.json.
#
#   scripts/pack-extension.sh <id> [--key <private.pem>] [--dry-run] [--out <dir>]
#
#   <id>          the extension id (see EXTENSIONS below)
#   --key         a PEM private key to sign with.
#   --dry-run     a real release is not being cut: a missing key is a note, not a failure; the
#                 git-tag/manifest-version check only runs when a matching tag ref is present;
#                 DV_EXTENSION_SIGNING_KEY is never used, signed or not, only an explicit --key.
#   --out         output directory (default: artifacts/extension-release/<id>)
#
# Every step fails loudly; nothing here is skipped silently.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# id|csproj directory (relative to repo root)|repo manifest path
EXTENSIONS=(
    "net.demoviewer.pack.stratbook|src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook|src/Extensions/StratBook/extension.json"
)

ID=""
KEY_PATH=""
DRY_RUN=0
OUT_BASE=""

while [ $# -gt 0 ]; do
    case "$1" in
        --key) KEY_PATH="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        --out) OUT_BASE="$2"; shift 2 ;;
        -h|--help)
            sed -n '2,15p' "$0"; exit 0 ;;
        *)
            if [ -n "$ID" ]; then
                echo "unexpected argument '$1'" >&2; exit 2
            fi
            ID="$1"; shift ;;
    esac
done

# Captured before the signing section can reassign KEY_PATH to a temp file: the verify override and
# the dry-run signing gate below both act only on a key the caller passed explicitly.
EXPLICIT_KEY_PATH="$KEY_PATH"

if [ -z "$ID" ]; then
    echo "usage: $0 <id> [--key <private.pem>] [--dry-run] [--out <dir>]" >&2
    echo "known ids: $(for e in "${EXTENSIONS[@]}"; do printf '%s ' "${e%%|*}"; done)" >&2
    exit 2
fi

CSPROJ_DIR=""
MANIFEST_PATH=""
for entry in "${EXTENSIONS[@]}"; do
    entry_id="${entry%%|*}"
    rest="${entry#*|}"
    if [ "$entry_id" = "$ID" ]; then
        CSPROJ_DIR="${rest%%|*}"
        MANIFEST_PATH="${rest#*|}"
    fi
done
if [ -z "$CSPROJ_DIR" ]; then
    echo "unknown extension id '$ID'. Known: $(for e in "${EXTENSIONS[@]}"; do printf '%s ' "${e%%|*}"; done)" >&2
    exit 2
fi
CSPROJ_FILE="$CSPROJ_DIR/$(basename "$CSPROJ_DIR").csproj"

OUT_BASE="${OUT_BASE:-artifacts/extension-release/$ID}"
STAGE_DIR="$OUT_BASE/stage"
mkdir -p "$OUT_BASE"
rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR"

echo "[pack-extension] id=$ID csproj=$CSPROJ_FILE manifest=$MANIFEST_PATH out=$OUT_BASE"

# ── Host values this build offers, for the compatibility report ───────────────────────────────────────
# Read from source rather than restated: a contract or CS2DemoKit bump that forgets one of these two
# files is a real defect this step should catch.
CONTRACT_HOST_FILE="src/App/DemoViewer.NET/Extensions/ExtensionHost.cs"
CONTRACT_MATCHES="$(grep -oE 'ContractVersion \{ get; \} = new\([0-9]+, *[0-9]+, *[0-9]+\)' "$CONTRACT_HOST_FILE" || true)"
CONTRACT_COUNT="$(printf '%s\n' "$CONTRACT_MATCHES" | grep -c . || true)"
if [ "$CONTRACT_COUNT" -ne 1 ]; then
    echo "error: expected exactly one 'ContractVersion' literal in $CONTRACT_HOST_FILE, found $CONTRACT_COUNT" >&2
    exit 2
fi
CONTRACT_VERSION="$(printf '%s\n' "$CONTRACT_MATCHES" | grep -oE '[0-9]+' | tr '\n' '.' | sed 's/\.$//')"

PACKAGES_PROPS="Directory.Packages.props"
KIT_MATCHES="$(grep -c 'PackageVersion Include="CS2DemoKit.Analysis"' "$PACKAGES_PROPS" || true)"
if [ "$KIT_MATCHES" -ne 1 ]; then
    echo "error: expected exactly one CS2DemoKit.Analysis pin in $PACKAGES_PROPS, found $KIT_MATCHES" >&2
    exit 2
fi
CS2DEMOKIT_VERSION="$(grep -oE 'PackageVersion Include="CS2DemoKit.Analysis" Version="[^"]+"' "$PACKAGES_PROPS" \
    | sed -E 's/.*Version="([^"]+)".*/\1/')"

echo "[pack-extension] host: contract=$CONTRACT_VERSION cs2demokit=$CS2DEMOKIT_VERSION"

# ── The version being released is the one Nerdbank.GitVersioning computes for the extension ───────────
# The repo manifest is a template: its "version" is the literal "{nbgv}" and the build stamps the real
# value from the extension directory's version.json (src/Extensions/ExtensionManifest.targets). The
# same nbgv call the build makes decides the version here, so the tag check runs before any build.
MANIFEST_TEMPLATE_VERSION="$(jq -r .version "$MANIFEST_PATH")"
if [ "$MANIFEST_TEMPLATE_VERSION" != "{nbgv}" ]; then
    echo "error: $MANIFEST_PATH must keep \"version\": \"{nbgv}\"; the build stamps the version from version.json." >&2
    exit 2
fi
if ! dotnet nbgv --version >/dev/null 2>&1; then
    dotnet tool restore >/dev/null
fi
MANIFEST_VERSION="$(dotnet nbgv get-version -p "$CSPROJ_DIR" -v NuGetPackageVersion)"
MANIFEST_ASSEMBLY="$(jq -r .assembly "$MANIFEST_PATH")"
ASSEMBLY_BASENAME="${MANIFEST_ASSEMBLY%.dll}"
echo "[pack-extension] version=$MANIFEST_VERSION (nbgv) assembly=$MANIFEST_ASSEMBLY"

# A tag push names the version being released; a dispatch run or a local invocation has none, which
# is not an error (pack-velopack.sh's own tag guard is the same shape). nbgv never reads the version
# from the tag, so a tag that disagrees with the computed version would ship one version under
# another's name.
TAG_REF="${GITHUB_REF:-}"
case "$TAG_REF" in
    refs/tags/extensions/"$ID"/v*)
        TAG_VER="${TAG_REF#refs/tags/extensions/"$ID"/v}"
        if [ "$TAG_VER" != "$MANIFEST_VERSION" ]; then
            echo "error: tag extensions/$ID/v$TAG_VER does not match the computed version $MANIFEST_VERSION." >&2
            echo "       The tag must be cut with 'dotnet nbgv tag -p $(dirname "$MANIFEST_PATH")' on the commit being released;" >&2
            echo "       re-tag to extensions/$ID/v$MANIFEST_VERSION." >&2
            exit 2
        fi
        echo "[pack-extension] tag/version agreement: extensions/$ID/v$TAG_VER == $MANIFEST_VERSION"
        # The tag itself is a public-release ref, so nbgv reads clean from any commit it is put on. A
        # release is cut from main only (strat-book-plugin.md §7.11); a real run refuses a tag whose
        # commit is not on origin/main. A dry run against a tag only reports it.
        if ! git rev-parse --verify -q origin/main >/dev/null; then
            echo "error: origin/main is not available, so the tagged commit cannot be checked against it." >&2
            exit 2
        fi
        if git merge-base --is-ancestor HEAD origin/main; then
            echo "[pack-extension] tagged commit is on origin/main"
        elif [ "$DRY_RUN" -eq 1 ]; then
            echo "[pack-extension] note: the tagged commit is not on origin/main; a real run would refuse it"
        else
            echo "error: the tagged commit $(git rev-parse --short HEAD) is not on origin/main; releases are cut from main." >&2
            exit 2
        fi
        ;;
    *)
        echo "[pack-extension] no extensions/$ID/v* tag ref present; skipping the tag/version check"
        ;;
esac

# ── Build the signing tool and the extension, Release, framework-dependent ────────────────────────────
# -getProperty alone does not build: it only evaluates. Build first, then query.
echo "[pack-extension] building tools/extension-signing"
dotnet build tools/extension-signing/ExtensionSigningTool.csproj -c Release -v q --nologo
TOOL_DLL="$(dotnet build tools/extension-signing/ExtensionSigningTool.csproj -c Release -getProperty:TargetPath -v q)"

echo "[pack-extension] building $CSPROJ_FILE"
dotnet build "$CSPROJ_FILE" -c Release -v q --nologo
BUILD_JSON="$(dotnet build "$CSPROJ_FILE" -c Release -getProperty:TargetDir -getProperty:AssemblyName -v q)"
TARGET_DIR="$(printf '%s' "$BUILD_JSON" | jq -r '.Properties.TargetDir')"
BUILT_ASSEMBLY_NAME="$(printf '%s' "$BUILD_JSON" | jq -r '.Properties.AssemblyName')"
if [ "$BUILT_ASSEMBLY_NAME" != "$ASSEMBLY_BASENAME" ]; then
    echo "error: the manifest's 'assembly' is '$MANIFEST_ASSEMBLY' but the project builds '$BUILT_ASSEMBLY_NAME.dll'." >&2
    exit 2
fi

DLL_PATH="${TARGET_DIR}${BUILT_ASSEMBLY_NAME}.dll"
DEPS_PATH="${TARGET_DIR}${BUILT_ASSEMBLY_NAME}.deps.json"
if [ ! -f "$DLL_PATH" ] || [ ! -f "$DEPS_PATH" ]; then
    echo "error: expected $DLL_PATH and $DEPS_PATH after build." >&2
    exit 2
fi

# ── Stage exactly what the extension project itself produces ──────────────────────────────────────────
# deps.json's own entry for this project (key "<AssemblyName>/<nbgv-version>") lists, under "runtime"
# and "resources", only the files this project's own compile output contributes; every referenced
# project and package is a separate entry. "native" or "runtimeTargets" on this same entry would mean
# a RID-specific asset, which this script refuses to package: the extension is managed-only, AnyCPU.
OWN_LIBRARY_JSON="$(jq --arg name "$BUILT_ASSEMBLY_NAME" '
    .targets[(.targets | keys[0])] as $libs
    | $libs | to_entries[] | select(.key | startswith($name + "/")) | .value
' "$DEPS_PATH")"
if [ -z "$OWN_LIBRARY_JSON" ] || [ "$OWN_LIBRARY_JSON" = "null" ]; then
    echo "error: $DEPS_PATH has no target library entry for $BUILT_ASSEMBLY_NAME." >&2
    exit 2
fi

HAS_NATIVE="$(printf '%s' "$OWN_LIBRARY_JSON" | jq -r 'has("native") or has("runtimeTargets")')"
if [ "$HAS_NATIVE" = "true" ]; then
    echo "error: $BUILT_ASSEMBLY_NAME ships a RID-specific native asset (deps.json has 'native' or" >&2
    echo "       'runtimeTargets' on its own library entry); this script packages a managed-only," >&2
    echo "       AnyCPU extension only." >&2
    exit 1
fi

# Copy every file deps.json's own-library entry lists under "runtime" and "resources", each at its own
# relative path: this is "the project's own output" without a hand-maintained file list.
stage_from_deps() {
    local member="$1"
    local paths
    paths="$(printf '%s' "$OWN_LIBRARY_JSON" | jq -r --arg m "$member" '.[$m] // {} | keys[]')"
    [ -z "$paths" ] && return 0
    while IFS= read -r rel; do
        [ -z "$rel" ] && continue
        mkdir -p "$STAGE_DIR/$(dirname "$rel")"
        cp "${TARGET_DIR}${rel}" "$STAGE_DIR/$rel"
    done <<< "$paths"
}
stage_from_deps runtime
stage_from_deps resources
if [ ! -f "$STAGE_DIR/$BUILT_ASSEMBLY_NAME.dll" ]; then
    echo "error: deps.json's 'runtime' entry for $BUILT_ASSEMBLY_NAME staged no $BUILT_ASSEMBLY_NAME.dll." >&2
    exit 2
fi

# deps.json does not track the XML doc file; stage it when the build produced one.
XML_PATH="${TARGET_DIR}${BUILT_ASSEMBLY_NAME}.xml"
if [ -f "$XML_PATH" ]; then
    cp "$XML_PATH" "$STAGE_DIR/"
fi

# The stamped copy beside the DLL is the manifest that ships. Three checks tie it to its sources: its
# version is the one nbgv computed above, it equals the embedded copy byte for byte, and it equals the
# repo template with the placeholder replaced (so a template edit without a rebuild is caught).
BUILT_MANIFEST_PATH="${TARGET_DIR}extension.json"
if [ ! -f "$BUILT_MANIFEST_PATH" ]; then
    echo "error: the build left no stamped extension.json beside $DLL_PATH." >&2
    exit 2
fi
BUILT_VERSION="$(jq -r .version "$BUILT_MANIFEST_PATH")"
if [ "$BUILT_VERSION" != "$MANIFEST_VERSION" ]; then
    echo "error: the stamped manifest says $BUILT_VERSION but nbgv computed $MANIFEST_VERSION." >&2
    exit 2
fi
EMBEDDED_MANIFEST_PATH="$OUT_BASE/embedded-manifest.json"
dotnet "$TOOL_DLL" manifest "$DLL_PATH" > "$EMBEDDED_MANIFEST_PATH"
if ! cmp -s "$EMBEDDED_MANIFEST_PATH" "$BUILT_MANIFEST_PATH"; then
    echo "error: the manifest embedded in $DLL_PATH does not match the stamped copy $BUILT_MANIFEST_PATH." >&2
    exit 2
fi
rm -f "$EMBEDDED_MANIFEST_PATH"
EXPECTED_MANIFEST_PATH="$OUT_BASE/expected-manifest.json"
sed "s/\"{nbgv}\"/\"$MANIFEST_VERSION\"/" "$MANIFEST_PATH" > "$EXPECTED_MANIFEST_PATH"
if ! cmp -s "$EXPECTED_MANIFEST_PATH" "$BUILT_MANIFEST_PATH"; then
    echo "error: the stamped manifest $BUILT_MANIFEST_PATH is not the template $MANIFEST_PATH with its version filled in." >&2
    echo "       rebuild after editing extension.json." >&2
    exit 2
fi
rm -f "$EXPECTED_MANIFEST_PATH"
cp "$BUILT_MANIFEST_PATH" "$STAGE_DIR/extension.json"
echo "[pack-extension] stamped manifest $MANIFEST_VERSION matches the embedded copy and the template"

echo "[pack-extension] staged:"
(cd "$STAGE_DIR" && find . -type f | sort | sed 's/^/  /')

# ── Compatibility report ───────────────────────────────────────────────────────────────────────────────
echo "[pack-extension] compatibility report:"
if ! dotnet "$TOOL_DLL" report "$STAGE_DIR/extension.json" --contract "$CONTRACT_VERSION" --cs2demokit "$CS2DEMOKIT_VERSION"; then
    echo "error: this extension build is not compatible with its own host values; refusing to release it." >&2
    exit 1
fi

# ── Signing ─────────────────────────────────────────────────────────────────────────────────────────────
KEY_TMP=""
cleanup() {
    [ -n "$KEY_TMP" ] && [ -f "$KEY_TMP" ] && rm -f "$KEY_TMP"
    return 0
}
trap cleanup EXIT

# A real release always verifies against PublisherKeys.Current with no override, even with --key; only
# a dry run with an explicit --key (an ephemeral keygen'd key, never in PublisherKeys.Current) checks
# against that key's own public half. Left empty rather than built as an array: an empty array hits
# bash 3.2's nounset-on-empty-array bug, still the default /bin/bash on macOS.
VERIFY_KEY_PATH=""
if [ "$DRY_RUN" -eq 1 ] && [ -n "$EXPLICIT_KEY_PATH" ]; then
    VERIFY_KEY_PATH="$EXPLICIT_KEY_PATH"
fi

# DV_EXTENSION_SIGNING_KEY is never read in a dry run, signed or not: a dry run signs only with an
# explicit --key, so the production key can never end up on an unpublished artifact.
if [ "$DRY_RUN" -ne 1 ] && [ -z "$KEY_PATH" ] && [ -n "${DV_EXTENSION_SIGNING_KEY:-}" ]; then
    KEY_TMP="$(mktemp)"
    chmod 600 "$KEY_TMP"
    printf '%s' "$DV_EXTENSION_SIGNING_KEY" > "$KEY_TMP"
    KEY_PATH="$KEY_TMP"
fi

if [ -z "$KEY_PATH" ]; then
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "[pack-extension] no --key given (DV_EXTENSION_SIGNING_KEY is ignored in dry-run mode); shipping an unsigned zip"
    else
        echo "error: no signing key available. Pass --key <private.pem>, or set DV_EXTENSION_SIGNING_KEY" >&2
        echo "       (the owner stores the private key's PEM as that GitHub repo secret; see" >&2
        echo "       docs/architecture/strat-book-plugin.md §7.9's owner action)." >&2
        exit 1
    fi
else
    echo "[pack-extension] signing"
    dotnet "$TOOL_DLL" sign "$STAGE_DIR" --key "$KEY_PATH" --force
    if [ -n "$VERIFY_KEY_PATH" ]; then
        dotnet "$TOOL_DLL" verify "$STAGE_DIR" --key "$VERIFY_KEY_PATH"
    else
        dotnet "$TOOL_DLL" verify "$STAGE_DIR"
    fi
fi

# ── Zip, deterministically ─────────────────────────────────────────────────────────────────────────────
ZIP_NAME="${ASSEMBLY_BASENAME}-${MANIFEST_VERSION}.zip"
ZIP_PATH="$OUT_BASE/$ZIP_NAME"
dotnet "$TOOL_DLL" zip "$STAGE_DIR" --out "$ZIP_PATH"

# Verify the actual zip contents, not just the staged directory: what the loader extracts is this
# archive, and a zip tool bug would otherwise go unnoticed.
UNZIP_DIR="$OUT_BASE/unzip-check"
rm -rf "$UNZIP_DIR"
mkdir -p "$UNZIP_DIR"
unzip -q "$ZIP_PATH" -d "$UNZIP_DIR"
STAGED_LIST="$(cd "$STAGE_DIR" && find . -type f | sort)"
UNZIPPED_LIST="$(cd "$UNZIP_DIR" && find . -type f | sort)"
if [ "$STAGED_LIST" != "$UNZIPPED_LIST" ]; then
    echo "error: the zip's file list does not match the staged directory's." >&2
    echo "staged:"; printf '%s\n' "$STAGED_LIST" >&2
    echo "zipped:"; printf '%s\n' "$UNZIPPED_LIST" >&2
    exit 2
fi
if [ -n "$KEY_PATH" ]; then
    if [ -n "$VERIFY_KEY_PATH" ]; then
        dotnet "$TOOL_DLL" verify "$UNZIP_DIR" --key "$VERIFY_KEY_PATH"
    else
        dotnet "$TOOL_DLL" verify "$UNZIP_DIR"
    fi
fi
rm -rf "$UNZIP_DIR"

if command -v sha256sum >/dev/null 2>&1; then
    SHA256="$(sha256sum "$ZIP_PATH" | cut -d' ' -f1)"
else
    SHA256="$(shasum -a 256 "$ZIP_PATH" | cut -d' ' -f1)"
fi
SIZE="$(wc -c < "$ZIP_PATH" | tr -d ' ')"
printf '%s  %s\n' "$SHA256" "$ZIP_NAME" > "$ZIP_PATH.sha256"

echo "[pack-extension] zip: $ZIP_PATH ($SIZE bytes, sha256 $SHA256)"

# ── Feed entry fragment (strat-book-plugin.md §7.10) ───────────────────────────────────────────────────
REPO_URL="${DV_REPO_URL:-https://github.com/sid2934/DemoViewer.NET}"
RELEASE_TAG="${ID}-v${MANIFEST_VERSION}"
ASSET_URL="${REPO_URL}/releases/download/${RELEASE_TAG}/${ZIP_NAME}"
PUBLISHED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
FEED_ENTRY_PATH="$OUT_BASE/feed-entry.json"

jq -n \
    --argjson manifest "$(cat "$STAGE_DIR/extension.json")" \
    --arg version "$MANIFEST_VERSION" \
    --arg url "$ASSET_URL" \
    --arg sha256 "$SHA256" \
    --argjson size "$SIZE" \
    --arg publishedAt "$PUBLISHED_AT" \
    '{version: $version, manifest: $manifest, url: $url, sha256: $sha256, size: $size, publishedAt: $publishedAt}' \
    > "$FEED_ENTRY_PATH"

echo "[pack-extension] feed entry: $FEED_ENTRY_PATH"

# Self-test: merging the fragment into "no existing feed" and feed-checking the result proves it
# parses as a real ExtensionFeed entry, not merely as JSON.
NO_EXISTING_FEED="$OUT_BASE/.no-existing-feed.json"
rm -f "$NO_EXISTING_FEED"
FEED_CHECK_PATH="$OUT_BASE/feed-entry-check.json"
dotnet "$TOOL_DLL" feed-merge "$NO_EXISTING_FEED" "$FEED_ENTRY_PATH" --id "$ID" --out "$FEED_CHECK_PATH"
dotnet "$TOOL_DLL" feed-check "$FEED_CHECK_PATH"
rm -f "$FEED_CHECK_PATH"

if [ "$DRY_RUN" -eq 1 ]; then
    echo "[pack-extension] dry run complete: $ID $MANIFEST_VERSION"
else
    echo "[pack-extension] done: $ID $MANIFEST_VERSION"
fi

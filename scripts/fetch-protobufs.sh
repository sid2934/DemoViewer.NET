#!/bin/bash
# Fetch ONLY the protobuf definitions the build needs, at the pinned submodule SHAs.
#
#   ./scripts/fetch-protobufs.sh
#
# Why this exists instead of `submodules: recursive` in the workflows:
#
# The build consumes exactly one directory — cs2-opendocs/data/Protobufs, 42 files, ~420 KB —
# for Grpc.Tools codegen (see Cs2DemoKit.Parser.csproj). But that directory lives in a
# SUB-submodule: cs2-opendocs/data -> SteamDatabase/GameTracking-CS2, a repo that tracks the
# whole game. A recursive checkout therefore clones its entire history: 3.2 GB of objects and
# a 177 MB working tree, of which we read 420 KB. On a hosted runner that checkout measured
# 13m18s — the single largest step in the CI run.
#
# This fetches the same content, at the same pinned commits, in about a second:
#   - --filter=blob:none  : no blobs until needed
#   - --depth 1           : the pinned commit only, no history
#   - sparse-checkout     : materialize Protobufs/ and nothing else
#
# Correctness note: this honors the SAME pins a recursive checkout would. It reads the gitlink
# SHAs out of the tree rather than taking a branch tip, so the protos are byte-identical to
# what `git submodule update --init --recursive` produces. SchemaSnapshotTests pins the
# cs2-opendocs SHA independently and still guards drift.
#
# Local use: this works for a dev checkout too, and is a lot kinder than the recursive form.
# You only need the full submodule if you are browsing the schema dumps or running Codegen
# (which reads cs2-opendocs/docs/gameevents_schema.json).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

GAMETRACKING_URL="https://github.com/SteamDatabase/GameTracking-CS2"

# 0. Idempotent no-op if the protos are already here. Matters because a developer running this
#    in an existing checkout must not have their full submodule quietly replaced by a sparse
#    one — `sparse-checkout set` would evict the 177 MB of schema dumps they may be using.
if ls cs2-opendocs/data/Protobufs/*.proto >/dev/null 2>&1; then
    echo "==> protos already present in cs2-opendocs/data/Protobufs — nothing to do"
    exit 0
fi

# 1. cs2-opendocs itself — our own repo, small (~8 MB without data/). Shallow, no recursion:
#    we do NOT want it dragging in data/ the expensive way.
echo "==> cs2-opendocs (shallow, non-recursive)"
git submodule update --init --depth 1 cs2-opendocs

# 2. Read the pinned data/ SHA from cs2-opendocs' tree. This is the gitlink a recursive
#    checkout would resolve, so we land on exactly the same commit.
DATA_SHA="$(git -C cs2-opendocs rev-parse HEAD:data)"
echo "==> data/ pinned at $DATA_SHA"

# 3. Sparse + shallow + blobless fetch of just Protobufs/ at that commit.
DEST="cs2-opendocs/data"
# -e, not -d: an initialized submodule stores .git as a FILE pointing into .git/modules, so a
# -d test reads as "no repo here" and would delete a real checkout.
if [ ! -e "$DEST/.git" ]; then
    mkdir -p "$DEST"
    git -C "$DEST" init -q
    git -C "$DEST" remote add origin "$GAMETRACKING_URL"
fi
git -C "$DEST" sparse-checkout init --cone >/dev/null 2>&1 || true
git -C "$DEST" sparse-checkout set Protobufs
git -C "$DEST" fetch --depth 1 --filter=blob:none origin "$DATA_SHA"
git -C "$DEST" checkout -q FETCH_HEAD

COUNT="$(ls "$DEST"/Protobufs/*.proto 2>/dev/null | wc -l | tr -d ' ')"
if [ "$COUNT" -eq 0 ]; then
    echo "ERROR: no .proto files materialized in $DEST/Protobufs — the build will fail." >&2
    exit 1
fi
echo "==> $COUNT .proto files ready in $DEST/Protobufs"

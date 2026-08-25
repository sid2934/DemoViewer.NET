#!/usr/bin/env sh
# Convenience wrapper around the dv2d CLI (docs/playback2d-v2/dv2d.md).
#
#   scripts/dv2d.sh render --fixture tests/fixtures/playback2d/scenes/duel-mirage-b.scene.json --out /tmp/f.png
#   scripts/dv2d.sh golden verify
#   scripts/dv2d.sh bench --name duel-mirage-b --frames 512 --gate
#
# Exit codes pass through unchanged — in particular 4 means "a gate failed", which is the only code
# CI treats as "the change is bad".
exec dotnet run -c Release --project "$(dirname "$0")/../tools/DemoViewer.NET.Playback2D.Cli" -- "$@"

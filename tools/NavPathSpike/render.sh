#!/usr/bin/env sh
# Runs the pathing spike and renders each map's straight-versus-routed ink over its radar.
#   tools/NavPathSpike/render.sh [outDir]
set -e
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:-/tmp/navspike}"
mkdir -p "$out"
dotnet run -c Release --project "$root/tools/NavPathSpike" -- "$root/assets" "$out"
dotnet build "$root/tools/DemoViewer.NET.Playback2D.Cli" -c Release -v q -nologo >/dev/null
dv2d="$root/artifacts/bin/DemoViewer.NET.Playback2D.Cli/release/dv2d"
for m in de_dust2 de_mirage de_nuke; do
  sed "s/de_synthetic/$m/g" "$root/tests/fixtures/playback2d/scenes/synthetic-empty.scene.json" > "$out/$m.scene.json"
  "$dv2d" render --fixture "$out/$m.scene.json" --ink "$out/$m.dvann.json" --assets "$root/assets" \
    --camera fit-map --size 1400x1400 --layers radar,annotations --out "$out/$m.png" --quiet
done
"$dv2d" render --fixture "$out/de_dust2.scene.json" --ink "$out/de_dust2.dvann.json" --assets "$root/assets" \
  --camera fixed:900,800,4 --size 1000x1000 --layers radar,zones,annotations --out "$out/de_dust2-long-doors.png" --quiet
"$dv2d" render --fixture "$out/de_mirage.scene.json" --ink "$out/de_mirage.dvann.json" --assets "$root/assets" \
  --camera fixed:-850,-700,3.5 --size 1000x1000 --layers radar,zones,annotations --out "$out/de_mirage-window.png" --quiet

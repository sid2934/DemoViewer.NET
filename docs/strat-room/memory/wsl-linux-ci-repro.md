---
name: wsl-linux-ci-repro
description: Reproduce this repo's Linux CI failures locally in WSL Ubuntu instead of pushing to CI; the two gotchas that make it work
metadata:
  type: reference
---

`wsl.exe -d Ubuntu` on this machine has .NET SDK 10.0.302 at `/home/austin/.dotnet` and fontconfig
installed, and sees the repo at `/mnt/c/dev/DemoViewer.NET`. It reproduces Linux-only CI failures
that Windows cannot, which turned multi-minute CI round-trips into local iteration during the
Avalonia 12 bump.

**Two gotchas, both of which cost time the first go:**

1. **Isolate the build output.** The repo uses the .NET artifacts layout, so a Linux build would
   overwrite the Windows one in `artifacts/`. Pass
   `-p:ArtifactsPath=/home/austin/dv-artifacts-linux` to every `dotnet build`/`run`.
2. **Drive it from a script file, not an inline command.** Git Bash mangles `wsl.exe` invocations
   two ways: it path-translates `/mnt/c/...` into `C:/Program Files/Git/mnt/c/...` unless
   `MSYS_NO_PATHCONV=1` is set, and the inherited Windows `PATH` contains spaces and parens that
   break `PATH=$DOTNET_ROOT:$PATH` inside the shell. Write the script to the scratchpad, `dos2unix`
   it, and run `MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu -- bash /mnt/c/...`. Set `PATH` absolutely
   inside the script rather than appending to the inherited one.

**What it is good for:** `scripts/test.sh` tiers, and `dv2d bench --name <fixture> --perf [--cpu]`
for a per-layer render breakdown. `--cpu` pins the rasteriser so the GL backend is not a variable,
and it is the path `playback2d-budget` measures.

**Note the tier gap that made CI find things local runs did not:** `Category=Budget` is excluded
from BOTH the fast and standard tiers, and `library-tests` runs LiveSync at **full**. Running
`-t standard` locally and calling a branch verified misses both lanes.

Related: [[skia3-linux-raster-cost]], [[bench-run-variance]]

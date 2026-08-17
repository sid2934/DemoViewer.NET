#!/usr/bin/env python3
"""Check whether a packaged artifact carries a build-machine path.

    ./scripts/scan-release-artifacts.py artifacts/velopack/<rid>/*.nupkg ...

MANUAL tool. This was briefly a per-RID gate in release.yml and was removed after one run
(the Windows false positive below). Run it against a local pack when a dependency
changes, or before a release you want to be certain about.

BEFORE RE-WIRING IT INTO CI, read this: on Windows it flags
`DemoViewer.NET.Desktop_ExecutionStub.exe`, which is NOT ours. vpk copies its own prebuilt
`vendor/stub.exe` and renames it after the app, so the FIRST_PARTY name globs below claim a
Velopack binary. Its 85 `C:\\Users\\runneradmin\\.rustup\\...` hits are Velopack's CI building
their Rust stub — identical to vpk 1.2.0's vendored copy. Add '*_ExecutionStub.exe' to a skip
list first, or the gate fails every Windows release on a third-party file.

Context: installers are published to a PUBLIC repo, and
every assembly records the absolute path of its .pdb in the CodeView debug directory — so a
build that misses `ContinuousIntegrationBuild` ships the builder's home directory to every
user. That happened: the pre-fix v0.5.0 package carried 822 occurrences across 28 entries.

Two design choices that make this gate correct rather than merely present:

1. **Scoped to first-party + vendored natives.** Asserting "no /Users/ anywhere" would be
   permanently red: Avalonia, Microsoft's runtime and Velopack's own UpdateMac legitimately
   carry their CI agents' paths (612 occurrences in a clean osx package). Those are neither
   ours to fix nor identifying. Only files WE produce or ship as natives are in scope.

2. **No developer name is hardcoded.** The invariant is "no absolute home path in a
   first-party binary", which is true on every machine — including CI, where the builder is
   `runner` and a hardcoded personal name would silently never match.

Byte-level throughout: BSD grep under a UTF-8 locale silently declines to match inside binary
files, which has produced false negatives in this project and in two upstream repos.
"""
import fnmatch
import os
import sys
import zipfile

HOME_PATHS = [b'/Users/', b'/home/', b'/root/', b'C:\\Users\\']

# Files we build or ship as natives. Everything else is third-party and out of scope.
FIRST_PARTY = [
    'DemoViewer.NET*.dll', 'DemoViewer.NET*.exe', 'DemoViewer.NET*.pdb',
    'CS2OpenDev.Sdk.dll', 'Cs2VideoGenerator.Core.dll',
    'mock_server', 'mock_server.exe', 'server.dll', 'server.so', 'server.dylib',
]

# Non-binary payload. The bundled tour demo legitimately contains the tournament GOTV
# server's own '/home/steam/cs2/game/csgo' path, recorded by the organiser, not by us.
SKIP_SUFFIXES = ('.dem', '.png', '.json', '.yaml', '.tris', '.md', '.txt')


def in_scope(name: str) -> bool:
    base = os.path.basename(name)
    if base.lower().endswith(SKIP_SUFFIXES):
        return False
    return any(fnmatch.fnmatch(base, pat) for pat in FIRST_PARTY)


def scan(path: str):
    """-> (violations, scoped_count, normalized_marker_count)"""
    violations, scoped, norm = [], 0, 0
    if not zipfile.is_zipfile(path):
        print(f"  (not a zip container, skipped: {os.path.basename(path)})")
        return violations, scoped, norm
    z = zipfile.ZipFile(path)
    for n in z.namelist():
        if n.endswith('/') or not in_scope(n):
            continue
        scoped += 1
        d = z.read(n)
        norm += d.count(b'/_/')
        found = {p.decode('latin1'): d.count(p) for p in HOME_PATHS if d.count(p)}
        if found:
            violations.append((n, found))
    return violations, scoped, norm


def main(argv):
    if not argv:
        print("usage: scan-release-artifacts.py <artifact> [artifact ...]", file=sys.stderr)
        return 2

    failed = False
    for path in argv:
        if not os.path.exists(path):
            print(f"!! missing artifact: {path}", file=sys.stderr)
            failed = True
            continue
        violations, scoped, norm = scan(path)
        name = os.path.basename(path)
        if not scoped:
            print(f"  {name}: no first-party files in scope")
            continue
        if violations:
            failed = True
            print(f"FAIL {name}: {len(violations)}/{scoped} first-party files carry a build path")
            for n, f in violations:
                print(f"       {n}: {f}")
        else:
            # Positive signal: a clean result must come with evidence we read real binaries,
            # not that we scanned the wrong thing. ContinuousIntegrationBuild rewrites paths
            # to /_/..., so those markers should be present.
            print(f"  ok   {name}: {scoped} first-party files clean ('/_/' markers: {norm})")
            if norm == 0:
                print(f"       WARNING: no '/_/' markers — verify ContinuousIntegrationBuild applied.")

    if failed:
        print("\nA packaged artifact carries a build-machine path. These installers are published",
              file=sys.stderr)
        print("publicly; investigate the leak before overriding.", file=sys.stderr)
        return 1
    print("\nAll scanned artifacts clean.")
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))

#!/bin/bash
# Back-compat shim. The publish logic is now the parameterized, self-contained-by-default
# scripts/publish.sh.
#
# This entry point preserves the original behavior: a FRAMEWORK-DEPENDENT win-x64 bundle
# for off-machine testing (the target box then needs the .NET 10 + ASP.NET Core 10
# runtimes). For a shipping self-contained bundle use:  ./scripts/publish.sh win-x64
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
exec ./publish.sh win-x64 --framework-dependent "$@"

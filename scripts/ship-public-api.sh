#!/usr/bin/env bash
# Moves every PublicAPI.Unshipped.txt entry of the SDK packages into PublicAPI.Shipped.txt, for the commit
# an sdk/v* release tag points at. release-sdk.yml refuses to publish while either Unshipped list has entries.
set -euo pipefail
cd "$(dirname "$0")/.."
for dir in src/App/DemoViewer.NET.Modules.Abstractions src/Sdk/DemoViewer.NET.Extensions.Sdk; do
    shipped="$dir/PublicAPI.Shipped.txt"
    unshipped="$dir/PublicAPI.Unshipped.txt"
    {
        echo '#nullable enable'
        { grep -v '^#nullable' "$shipped" || true; grep -v '^#nullable' "$unshipped" || true; } | grep -v '^$' | sort -u
    } > "$shipped.new"
    mv "$shipped.new" "$shipped"
    echo '#nullable enable' > "$unshipped"
    echo "$dir: $(grep -vc '^#nullable' "$shipped") shipped"
done

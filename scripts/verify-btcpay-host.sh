#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
expected=5d0745cae6be5d8210459e38813f317673aa97b8
actual="$(git -C "$repo_root/submodules/btcpayserver" rev-parse HEAD)"
if [ "$actual" != "$expected" ]; then
    echo "Build requires the pinned BTCPay Server v2.4.5 source. Run git submodule update --init submodules/btcpayserver." >&2
    exit 1
fi

if ! git -C "$repo_root/submodules/btcpayserver" diff --quiet HEAD --; then
    echo "Build requires an unmodified pinned BTCPay host. Review tracked source changes before building." >&2
    exit 1
fi

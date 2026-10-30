#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
expected=2868deedd9d911b715fec7306ea9cada36d86bff
actual="$(git -C "$repo_root/submodules/btcpayserver" rev-parse HEAD)"
if [ "$actual" != "$expected" ]; then
    echo "Build requires the pinned BTCPay Server v2.4.7 source. Run git submodule update --init submodules/btcpayserver." >&2
    exit 1
fi

if ! git -C "$repo_root/submodules/btcpayserver" diff --quiet HEAD --; then
    echo "Build requires an unmodified pinned BTCPay host. Review tracked source changes before building." >&2
    exit 1
fi

# .NET discovers new source and build configuration through globs/imports too.
# Ignore normal generated output, but do not call an injected build input an exact host.
untracked_inputs="$(git -C "$repo_root/submodules/btcpayserver" ls-files --others --exclude-standard -- \
    '*.cs' '*.cshtml' '*.csproj' '*.props' '*.targets' '*.rsp' 'NuGet.Config' 'global.json')"
if [ -n "$untracked_inputs" ]; then
    echo "Build requires no untracked source or build configuration in the pinned BTCPay host." >&2
    exit 1
fi
